using System.Text.Json;
using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:shell_exec</c> — executa um comando shell no workspace do
/// usuário (SPEC-20261007-chat-agent-tools RF-003). Antes de rodar passa
/// pelo <see cref="CommandRiskClassifier"/> — comandos Dangerous
/// (elevação, rede, fuga do workspace, binário desconhecido) são negados.
/// Mutável → gate de aprovação.
/// </summary>
public sealed class ShellExecBuiltinTool(ChatJobService jobs) : IBuiltinChatTool
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(300);

    /// <inheritdoc />
    public string Name => "shell_exec";

    /// <inheritdoc />
    public string Description =>
        "Run a shell command — call for builds, tests, git, CLI.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "Command to run in the workspace. Any command may run, incl. sudo/apt/curl — dangerous ones require user approval." },
            "sudo_password": { "type": "string", "description": "Sudo password when the user provided one via ask_user. Primed with 'sudo -S -v' so subsequent sudo calls in the same command reuse it." },
            "timeout_seconds": { "type": "integer", "description": "Timeout (max 300).", "default": 60 },
            "background": {
              "type": "boolean",
              "description": "true = run detached as a durable job; poll with job_output and kill with job_kill.",
              "default": false
            }
          },
          "required": ["command"]
        }
        """;

    /// <inheritdoc />
    public bool RequiresApproval => true;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var command = args.TryGetProperty("command", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(command))
        {
            return new BuiltinToolResult("Parâmetro 'command' é obrigatório.");
        }

        var assessment = CommandRiskClassifier.Classify(command, context.WorkspacePath);
        if (!assessment.Allowed)
        {
            var refuseText = $"Comando negado: {assessment.Reason}";
            // Binário fora da allowlist: pode ser só uma ferramenta não
            // instalada/mapeada — orienta o agente a sugerir a instalação
            // e pedir aprovação via ask_user em vez de desistir.
            if (assessment.Reason?.Contains("Binário desconhecido") == true)
            {
                refuseText += MissingCommandHint;
            }

            return new BuiltinToolResult(
                refuseText,
                new { risk = assessment.Level.ToString(), reason = assessment.Reason },
                Refused: true,
                RefuseReason: assessment.Reason);
        }

        var background = args.TryGetProperty("background", out var b)
            && b.ValueKind is JsonValueKind.True;
        if (background)
        {
            var job = await jobs.StartAsync(command, context, ct);
            var jobText = job.Status == ChatJobStatus.Running
                ? $"Job iniciado em background: {job.Id} (pid {job.Pid}). "
                  + "Consulte com job_output e mate com job_kill."
                : $"Job falhou ao iniciar: {job.Error}";
            return new BuiltinToolResult(jobText, new
            {
                jobId = job.Id,
                status = job.Status,
                pid = job.Pid,
            });
        }

        var timeout = DefaultTimeout;
        if (args.TryGetProperty("timeout_seconds", out var t) && t.TryGetInt32(out var secs))
        {
            timeout = TimeSpan.FromSeconds(Math.Clamp(secs, 1, (int)MaxTimeout.TotalSeconds));
        }

        Directory.CreateDirectory(context.WorkspacePath);

        // sudo: se o usuário forneceu a senha (via ask_user), alimenta
        // 'sudo -S -v' por stdin — o timestamp cacheado cobre os demais
        // sudo do mesmo sh -c.
        string? stdin = null;
        var sudoPassword = args.TryGetProperty("sudo_password", out var p)
            ? p.GetString()
            : null;
        if (!string.IsNullOrEmpty(sudoPassword) && command.Contains("sudo", StringComparison.Ordinal))
        {
            command = "sudo -S -p '' -v; " + command;
            stdin = sudoPassword + "\n";
        }

        var outcome = await ChatProcessRunner.RunAsync(
            command, context.WorkspacePath, timeout, stdin: stdin, ct: ct);

        var status = outcome.TimedOut
            ? $"timeout após {(int)timeout.TotalSeconds}s"
            : $"exit {outcome.ExitCode}";
        var text = $"[{status}]\n{outcome.Output}";
        if (outcome.Truncated)
        {
            text += "\n[saída truncada em 16KB]";
        }

        // Ferramenta ausente (exit 127 / "command not found" / "not
        // recognized"): orienta o agente a sugerir a instalação e pedir
        // aprovação via ask_user em vez de só reportar a falha.
        if (LooksLikeMissingCommand(outcome))
        {
            text += MissingCommandHint;
        }
        else if (NeedsSudoPassword(outcome))
        {
            text += SudoPasswordHint;
        }

        return new BuiltinToolResult(text, new
        {
            exitCode = outcome.ExitCode,
            timedOut = outcome.TimedOut,
            truncated = outcome.Truncated,
            risk = assessment.Level.ToString(),
        });
    }

    // Ferramenta ausente: orienta o agente a sugerir a instalação e
    // pedir aprovação via ask_user em vez de só reportar a falha.
    // sudo sem senha: orienta a perguntar a senha via ask_user e
    // reexecutar passando sudo_password.
    private const string SudoPasswordHint =
        "\n\nsudo pediu senha. Pergunte a senha ao usuário via ask_user e "
        + "reexecute o mesmo comando passando sudo_password.";

    /// <summary>Detecta prompts de senha do sudo no output.</summary>
    private static bool NeedsSudoPassword(ProcessOutcome outcome)
    {
        var output = outcome.Output;
        return output.Contains("a password is required", StringComparison.OrdinalIgnoreCase)
            || output.Contains("no password present", StringComparison.OrdinalIgnoreCase)
            || output.Contains("a terminal is required to read the password", StringComparison.OrdinalIgnoreCase)
            || output.Contains("password for", StringComparison.OrdinalIgnoreCase)
            || output.Contains("digite a senha", StringComparison.OrdinalIgnoreCase);
    }

    private const string MissingCommandHint =
        "\n\nO comando falhou porque uma ferramenta provavelmente não está "
        + "instalada ou não é permitida. Sugira o comando de instalação ao "
        + "usuário e pergunte (ask_user) se ele aprova instalar antes de "
        + "tentar de novo.";

    /// <summary>Detecta "comando não encontrado" nos shells comuns (bash/sh 127,
    /// cmd/pwsh "not recognized", "não encontrado").</summary>
    private static bool LooksLikeMissingCommand(ProcessOutcome outcome)
    {
        if (outcome.ExitCode is 127 or 9009)
        {
            return true;
        }

        var output = outcome.Output;
        return output.Contains("command not found", StringComparison.OrdinalIgnoreCase)
            || output.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
            || output.Contains("não encontrado", StringComparison.OrdinalIgnoreCase)
            || output.Contains("not found", StringComparison.OrdinalIgnoreCase)
            && output.Contains("No such file", StringComparison.OrdinalIgnoreCase);
    }
}

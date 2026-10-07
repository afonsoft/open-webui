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
            "command": { "type": "string", "description": "Command to run in the workspace." },
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
            return new BuiltinToolResult(
                $"Comando negado: {assessment.Reason}",
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
        var outcome = await ChatProcessRunner.RunAsync(
            command, context.WorkspacePath, timeout, ct: ct);

        var status = outcome.TimedOut
            ? $"timeout após {(int)timeout.TotalSeconds}s"
            : $"exit {outcome.ExitCode}";
        var text = $"[{status}]\n{outcome.Output}";
        if (outcome.Truncated)
        {
            text += "\n[saída truncada em 16KB]";
        }

        return new BuiltinToolResult(text, new
        {
            exitCode = outcome.ExitCode,
            timedOut = outcome.TimedOut,
            truncated = outcome.Truncated,
            risk = assessment.Level.ToString(),
        });
    }
}

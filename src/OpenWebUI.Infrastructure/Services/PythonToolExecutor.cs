using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Executa o código Python de uma tool — mesma convenção do upstream: a fonte
/// define <c>class Tools</c> cujos métodos públicos são as funções chamáveis.
/// O código roda em subprocess do interpretador configurado em
/// <c>Python:Path</c> (env <c>PYTHON_PATH</c>, default <c>python3</c>), com o
/// payload <c>{"function":…,"arguments":{…}}</c> no stdin e o resultado em uma
/// linha marcada no stdout — assim prints do código do usuário não corrompem
/// o retorno. Timeout de 30s; stderr e saída são truncados.
/// <para>Segurança: o código executa com os privilégios do servidor — semelhante
/// ao upstream, que o executa in-process. Restrinja quem pode criar tools.</para>
/// </summary>
public class PythonToolExecutor(IConfiguration configuration)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const int MaxOutputChars = 4000;
    private const string Marker = "__OWUI_RESULT__";

    /// <summary>
    /// Executa <paramref name="functionName"/> na fonte Python da tool e
    /// retorna o resultado serializado ou mensagem de erro para o modelo.
    /// </summary>
    public async Task<string> ExecuteAsync(
        Tool tool, string functionName, string argumentsJson, CancellationToken ct = default)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "openwebui-tools");
        Directory.CreateDirectory(workDir);
        var scriptPath = Path.Combine(workDir, $"tool-{Guid.NewGuid():N}.py");
        try
        {
            await File.WriteAllTextAsync(scriptPath, BuildScript(tool.Code ?? string.Empty), ct);
            return await RunAsync(scriptPath, functionName, argumentsJson, workDir, ct);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch (IOException) { }
        }
    }

    /// <summary>Interpretador Python configurado (Python:Path / PYTHON_PATH).</summary>
    private string PythonPath() =>
        configuration["Python:Path"]
        ?? configuration["PYTHON_PATH"]
        ?? "python3";

    /// <summary>
    /// Concatena a fonte da tool com o runner que instancia <c>Tools</c>,
    /// chama a função (sync ou async) e imprime o resultado marcado.
    /// </summary>
    private static string BuildScript(string userCode) => userCode + """

import sys as __owui_sys, json as __owui_json, inspect as __owui_inspect, asyncio as __owui_asyncio

def __owui_main__():
    try:
        req = __owui_json.loads(__owui_sys.stdin.read())
        fn = getattr(Tools(), req["function"])
        res = fn(**(req.get("arguments") or {}))
        if __owui_inspect.isawaitable(res):
            res = __owui_asyncio.run(res)
        print("__OWUI_RESULT__" + __owui_json.dumps({"result": res}, default=str))
    except Exception as exc:
        print("__OWUI_RESULT__" + __owui_json.dumps(
            {"error": f"{type(exc).__name__}: {exc}"}))

__owui_main__()
""";

    private async Task<string> RunAsync(
        string scriptPath, string functionName, string argumentsJson,
        string workDir, CancellationToken ct)
    {
        JsonElement arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<JsonElement>(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        }
        catch (JsonException)
        {
            arguments = JsonSerializer.Deserialize<JsonElement>("{}");
        }
        var payload = JsonSerializer.Serialize(new { function = functionName, arguments });

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(PythonPath(), scriptPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workDir,
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return $"Erro: interpretador Python '{PythonPath()}' não encontrado.";
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            KillTree(process);
            return $"Erro: tool '{functionName}' excedeu o tempo limite de {Timeout.TotalSeconds}s.";
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return ParseResult(functionName, stdout, stderr, process.ExitCode);
    }

    /// <summary>Extrai a linha marcada do stdout e interpreta o resultado.</summary>
    private static string ParseResult(string functionName, string stdout, string stderr, int exitCode)
    {
        var marked = stdout.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .LastOrDefault(l => l.StartsWith(Marker, StringComparison.Ordinal));
        if (marked is null)
        {
            var detail = stderr.Trim();
            if (detail.Length > MaxOutputChars)
            {
                detail = detail[..MaxOutputChars];
            }
            return string.IsNullOrEmpty(detail)
                ? $"Erro: tool '{functionName}' terminou com código {exitCode} sem resultado."
                : $"Erro na tool '{functionName}': {detail}";
        }

        try
        {
            using var doc = JsonDocument.Parse(marked[Marker.Length..]);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                return $"Erro na tool '{functionName}': {error.GetString()}";
            }
            var result = root.GetProperty("result");
            var text = result.ValueKind == JsonValueKind.String
                ? result.GetString() ?? string.Empty
                : result.GetRawText();
            return text.Length > MaxOutputChars ? text[..MaxOutputChars] : text;
        }
        catch (JsonException)
        {
            return $"Erro: resultado inválido da tool '{functionName}'.";
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}

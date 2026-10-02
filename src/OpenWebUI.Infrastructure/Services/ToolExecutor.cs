using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Executa tools registradas via HTTP POST server-side (URL nunca exposta
/// ao cliente) ou, quando a tool tem <see cref="Tool.Code"/>, em subprocess
/// Python (<see cref="PythonToolExecutor"/>, convenção class Tools do upstream).
/// Timeout de 30s; erro vira resultado de erro para o modelo.
/// </summary>
public class ToolExecutor(
    AppDbContext db,
    IHttpClientFactory httpClientFactory,
    PythonToolExecutor pythonExecutor)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const int MaxOutputChars = 4000;

    /// <summary>Carrega tools habilitadas do usuário pelos ids selecionados.</summary>
    /// <param name="userId">Dono das tools.</param>
    /// <param name="toolIds">Ids selecionados no chat.</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<List<Tool>> LoadEnabledAsync(
        string userId, IReadOnlyList<string> toolIds, CancellationToken ct = default)
    {
        if (toolIds.Count == 0)
        {
            return [];
        }
        return await db.Tools.AsNoTracking()
            .Where(t => toolIds.Contains(t.Id) && t.Enabled && t.UserId == userId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Executa a tool pelo nome da função chamada e retorna o texto do
    /// resultado (truncado) ou mensagem de erro para o modelo.
    /// </summary>
    /// <param name="tools">Tools habilitadas no contexto.</param>
    /// <param name="functionName">Nome da função pedida.</param>
    /// <param name="argumentsJson">Argumentos em JSON.</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<string> ExecuteAsync(
        IReadOnlyList<Tool> tools, string functionName, string argumentsJson,
        CancellationToken ct = default)
    {
        var tool = tools.FirstOrDefault(t => FunctionName(t) == functionName);
        if (tool is null)
        {
            return $"Erro: tool '{functionName}' não está habilitada neste chat.";
        }

        if (!string.IsNullOrWhiteSpace(tool.Code))
        {
            return await pythonExecutor.ExecuteAsync(tool, functionName, argumentsJson, ct);
        }

        try
        {
            using var http = httpClientFactory.CreateClient();
            http.Timeout = Timeout;
            using var response = await http.PostAsync(
                tool.Url,
                new StringContent(argumentsJson, System.Text.Encoding.UTF8, "application/json"),
                ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return $"Erro: tool '{functionName}' respondeu {(int)response.StatusCode}.";
            }
            return body.Length > MaxOutputChars ? body[..MaxOutputChars] : body;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"Erro ao executar tool '{functionName}': {ex.Message}";
        }
    }

    /// <summary>Extrai o nome da função do spec da tool.</summary>
    internal static string? FunctionName(Tool tool)
    {
        try
        {
            using var doc = JsonDocument.Parse(tool.SpecJson);
            return doc.RootElement.TryGetProperty("function", out var fn) &&
                   fn.TryGetProperty("name", out var name)
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Consome os endpoints SSE de chat — <c>/api/chat/completions</c> (direto,
/// legado) e o attach de runs desacopladas
/// <c>/api/v1/chats/{id}/runs/{runId}/stream</c> — produzindo deltas de
/// conteúdo. Erros do servidor viram exceção na enumeração.
/// </summary>
public class ChatStreamService(HttpClient http, AuthService auth)
{
    /// <summary>Resultado de arena da última requisição (payload {"arena": ...}), quando houver.</summary>
    public ArenaCompletionResult? LastArenaResult { get; private set; }

    /// <summary>
    /// Envia a requisição e produz os deltas de texto conforme chegam.
    /// Erros do servidor são produzidos como exceção na enumeração.
    /// </summary>
    /// <param name="request">Requisição de completion.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastArenaResult = null;

        using var httpRequest = auth.CreateRequest(HttpMethod.Post, "/api/chat/completions");
        httpRequest.SetBrowserResponseStreamingEnabled(true);
        httpRequest.Content = JsonContent.Create(request);

        using var response = await http.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "gerar resposta", ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var delta in ReadDeltasAsync(stream, ct))
        {
            yield return delta;
        }
    }

    /// <summary>
    /// Enfileira uma run desacoplada (SPEC-20261007-chat-detached-runs):
    /// a mensagem é persistida no servidor e a geração roda em background —
    /// sobrevive a reload/fechamento da aba. <paramref name="content"/> null
    /// regenera sobre o histórico existente.
    /// </summary>
    public async Task<ChatRunResponse> EnqueueRunAsync(
        string chatId,
        string? content,
        string model,
        IReadOnlyList<string>? fileIds = null,
        IReadOnlyList<string>? toolIds = null,
        bool? webSearch = null,
        CancellationToken ct = default)
    {
        using var httpRequest = auth.CreateRequest(
            HttpMethod.Post, $"/api/v1/chats/{chatId}/messages");
        httpRequest.Content = JsonContent.Create(new EnqueueChatRunRequest(
            content, model, fileIds, toolIds, WebSearch: webSearch));

        using var response = await http.SendAsync(httpRequest, ct);
        await EnsureSuccessAsync(response, "enviar mensagem", ct);
        return (await response.Content.ReadFromJsonAsync<ChatRunResponse>(ct))!;
    }

    /// <summary>Run ativa (queued/running) do chat, ou null.</summary>
    public async Task<ChatRunResponse?> GetActiveRunAsync(
        string chatId, CancellationToken ct = default)
    {
        using var httpRequest = auth.CreateRequest(
            HttpMethod.Get, $"/api/v1/chats/{chatId}/runs/active");
        using var response = await http.SendAsync(httpRequest, ct);
        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentLength is null or 0)
        {
            return null;
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<ChatRunResponse>(ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Pede a interrupção de uma run ativa.</summary>
    public async Task<bool> StopRunAsync(
        string chatId, string runId, CancellationToken ct = default)
    {
        using var httpRequest = auth.CreateRequest(
            HttpMethod.Post, $"/api/v1/chats/{chatId}/runs/{runId}/stop");
        using var response = await http.SendAsync(httpRequest, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Anexa ao stream de uma run: replay dos eventos com seq &gt;
    /// <paramref name="lastSeq"/> e depois os vivos até a run fechar.
    /// Produz os mesmos deltas de <see cref="StreamCompletionAsync"/>.
    /// </summary>
    public async IAsyncEnumerable<string> StreamRunAsync(
        string chatId,
        string runId,
        int lastSeq = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastArenaResult = null;

        using var httpRequest = auth.CreateRequest(
            HttpMethod.Get, $"/api/v1/chats/{chatId}/runs/{runId}/stream?lastSeq={lastSeq}");
        httpRequest.SetBrowserResponseStreamingEnabled(true);

        using var response = await http.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "anexar à run", ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var delta in ReadDeltasAsync(stream, ct))
        {
            yield return delta;
        }
    }

    /// <summary>Lê linhas `data:` do SSE e produz os deltas de conteúdo.</summary>
    private async IAsyncEnumerable<string> ReadDeltasAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
            {
                yield break;
            }

            string? delta = null;
            string? error = null;
            try
            {
                var node = JsonNode.Parse(payload);
                error = node?["error"]?.GetValue<string>();
                // Gateways podem emitir chunks sem choice (só role/usage/keepalive):
                // indexar um array vazio lança ArgumentOutOfRangeException.
                var choices = node?["choices"] as JsonArray;
                delta = choices is { Count: > 0 }
                    ? choices[0]?["delta"]?["content"]?.GetValue<string>()
                    : null;
                var arena = node?["arena"];
                if (arena is not null)
                {
                    LastArenaResult = new ArenaCompletionResult(
                        arena["battle_id"]?.GetValue<string>() ?? string.Empty,
                        (arena["responses"] as JsonArray ?? [])
                            .Select(r => new ArenaCompletionResponse(
                                r?["label"]?.GetValue<string>() ?? "?",
                                r?["content"]?.GetValue<string>() ?? string.Empty))
                            .ToList());
                }
            }
            catch (JsonException)
            {
                continue;
            }

            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }

            if (!string.IsNullOrEmpty(delta))
            {
                yield return delta;
            }
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"Erro {(int)response.StatusCode} ao {action}: {body}");
    }
}

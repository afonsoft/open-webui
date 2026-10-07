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
    /// <summary>
    /// Evento tipado do stream de uma run (SPEC-20261007-chat-tool-streaming):
    /// delta de texto, tool_call/tool_result, fase de status ou pedido de
    /// aprovação. O endpoint legado só produz <see cref="Delta"/>.
    /// </summary>
    public abstract record ChatStreamEvent
    {
        /// <summary>Pedaco de texto do assistant.</summary>
        public sealed record Delta(string Text) : ChatStreamEvent;

        /// <summary>Tool call iniciada (antes de executar).</summary>
        public sealed record ToolCall(RunToolCallEvent Call) : ChatStreamEvent;

        /// <summary>Resultado/negação de uma tool call.</summary>
        public sealed record ToolResult(RunToolResultEvent Result) : ChatStreamEvent;

        /// <summary>Fase da run (generating|running_tool|awaiting_approval).</summary>
        public sealed record Phase(RunPhaseEvent Status) : ChatStreamEvent;

        /// <summary>Aprovação pedida — a run pausou esperando decisão.</summary>
        public sealed record ApprovalAsked(RunApprovalAskedEvent Asked) : ChatStreamEvent;

        /// <summary>
        /// Snapshot da lista de tarefas da run (evento <c>tasks</c> —
        /// SPEC-20261007-chat-agent-parity RF-010, emitido por todo_write).
        /// </summary>
        public sealed record Tasks(RunTasksEvent Snapshot) : ChatStreamEvent;
    }

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

    /// <summary>Suspende uma run ativa — o executor bloqueia no próximo checkpoint.</summary>
    public async Task<bool> PauseRunAsync(
        string chatId, string runId, CancellationToken ct = default)
    {
        using var httpRequest = auth.CreateRequest(
            HttpMethod.Post, $"/api/v1/chats/{chatId}/runs/{runId}/pause");
        using var response = await http.SendAsync(httpRequest, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Retoma uma run pausada de onde ela parou.</summary>
    public async Task<bool> ResumeRunAsync(
        string chatId, string runId, CancellationToken ct = default)
    {
        using var httpRequest = auth.CreateRequest(
            HttpMethod.Post, $"/api/v1/chats/{chatId}/runs/{runId}/resume");
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
        await foreach (var evt in StreamRunEventsAsync(chatId, runId, lastSeq, ct))
        {
            if (evt is ChatStreamEvent.Delta delta)
            {
                yield return delta.Text;
            }
        }
    }

    /// <summary>
    /// Anexa ao stream de uma run produzindo os eventos tipados
    /// (<see cref="ChatStreamEvent"/>): deltas de texto mais tool_call,
    /// tool_result, fase de status e pedidos de aprovação
    /// (SPEC-20261007-chat-tool-streaming). Eventos desconhecidos são
    /// ignorados (forward-compat).
    /// </summary>
    public async IAsyncEnumerable<ChatStreamEvent> StreamRunEventsAsync(
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
        await foreach (var evt in ReadEventsAsync(stream, ct))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// Envia a decisão do usuário sobre uma aprovação pendente
    /// (<c>approve</c>|<c>deny</c>; <paramref name="remember"/> vale nesta
    /// conversa; <paramref name="message"/> é instrução opcional numa
    /// negação — vira o resultado da tool, SPEC-20261007-chat-agent-ux).
    /// Retorna false quando a call não está mais pendente.
    /// </summary>
    public async Task<bool> DecideApprovalAsync(
        string chatId, string runId, string callId,
        string decision, bool remember = false, string? message = null,
        CancellationToken ct = default)
    {
        using var httpRequest = auth.CreateRequest(
            HttpMethod.Post,
            $"/api/v1/chats/{chatId}/runs/{runId}/approvals/{callId}");
        httpRequest.Content = JsonContent.Create(
            new RunApprovalDecisionRequest(decision, remember, message));
        using var response = await http.SendAsync(httpRequest, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Lê linhas `data:` do SSE e produz os deltas de conteúdo.</summary>
    private async IAsyncEnumerable<string> ReadDeltasAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in ReadEventsAsync(stream, ct))
        {
            if (evt is ChatStreamEvent.Delta delta)
            {
                yield return delta.Text;
            }
        }
    }

    /// <summary>
    /// Parser SSE: lê pares `event:`/`data:` e produz
    /// <see cref="ChatStreamEvent"/>s — `data:` sem `event:` segue o
    /// formato OpenAI (deltas, erro, arena); `data: [DONE]` encerra.
    /// </summary>
    private async IAsyncEnumerable<ChatStreamEvent> ReadEventsAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        string? eventName = null;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }
            if (line.StartsWith("id:", StringComparison.Ordinal)
                || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
            {
                yield break;
            }

            var evt = eventName;
            eventName = null;
            ChatStreamEvent? produced = null;
            string? error = null;
            try
            {
                var node = JsonNode.Parse(payload);
                if (node is null)
                {
                    continue;
                }

                switch (evt)
                {
                    case "tool_call":
                        produced = new ChatStreamEvent.ToolCall(
                            new RunToolCallEvent(
                                node["id"]?.GetValue<string>() ?? string.Empty,
                                node["name"]?.GetValue<string>() ?? "tool",
                                node["argsPreview"]?.GetValue<string>()));
                        break;
                    case "tool_result":
                        produced = new ChatStreamEvent.ToolResult(
                            new RunToolResultEvent(
                                node["id"]?.GetValue<string>() ?? string.Empty,
                                node["name"]?.GetValue<string>() ?? "tool",
                                node["ok"]?.GetValue<bool>() ?? false,
                                node["preview"]?.GetValue<string>(),
                                node["imagePath"]?.GetValue<string>(),
                                node["denied"]?.GetValue<bool>() ?? false));
                        break;
                    case "approval_asked":
                        produced = new ChatStreamEvent.ApprovalAsked(
                            new RunApprovalAskedEvent(
                                node["callId"]?.GetValue<string>() ?? string.Empty,
                                node["toolName"]?.GetValue<string>() ?? "tool",
                                node["kind"]?.GetValue<string>() ?? "http",
                                node["argsPreview"]?.GetValue<string>()));
                        break;
                    case "tasks":
                    {
                        var arr = node as JsonArray ?? node["tasks"] as JsonArray;
                        var items = (arr ?? [])
                            .Select(t => new RunTaskItem(
                                t?["id"]?.GetValue<string>() ?? string.Empty,
                                t?["content"]?.GetValue<string>() ?? string.Empty,
                                t?["status"]?.GetValue<string>() ?? "pending"))
                            .ToList();
                        produced = new ChatStreamEvent.Tasks(new RunTasksEvent(items));
                        break;
                    }
                    case "status":
                        if (node["phase"]?.GetValue<string>() is { } phase)
                        {
                            produced = new ChatStreamEvent.Phase(
                                new RunPhaseEvent(
                                    phase, node["label"]?.GetValue<string>()));
                        }
                        break;
                    default:
                        error = node["error"]?.GetValue<string>();
                        // Gateways podem emitir chunks sem choice (só role/usage/keepalive):
                        // indexar um array vazio lança ArgumentOutOfRangeException.
                        var choices = node["choices"] as JsonArray;
                        var delta = choices is { Count: > 0 }
                            ? choices[0]?["delta"]?["content"]?.GetValue<string>()
                            : null;
                        var arena = node["arena"];
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
                        if (!string.IsNullOrEmpty(delta))
                        {
                            produced = new ChatStreamEvent.Delta(delta);
                        }
                        break;
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
            if (produced is not null)
            {
                yield return produced;
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

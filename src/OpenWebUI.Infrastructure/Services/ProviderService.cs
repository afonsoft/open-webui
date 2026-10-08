using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Resolve conexões com provedores de IA (Ollama e APIs compatíveis com OpenAI),
/// lista modelos e transmite completions no formato SSE da OpenAI.
/// </summary>
public class ProviderService(
    IHttpClientFactory httpClientFactory,
    ConfigService config,
    ILogger<ProviderService> logger,
    IMemoryCache cache)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Cache curto da lista de modelos: sem ele, cada mensagem de chat
    // (ResolveProviderAsync) fazia GET /models em TODAS as conexões.
    private static readonly TimeSpan ModelsCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>Lista modelos de todas as conexões configuradas.</summary>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var connections = await config.GetConnectionsAsync(ct);
        var cacheKey = $"provider-models:{ConnectionsFingerprint(connections)}";
        if (cache.TryGetValue(cacheKey, out List<ModelInfo>? cached) && cached is not null)
        {
            return cached;
        }

        var models = await FetchAllModelsAsync(connections, ct);
        cache.Set(cacheKey, models, ModelsCacheTtl);
        return models;
    }

    private async Task<List<ModelInfo>> FetchAllModelsAsync(
        ConnectionsConfig connections, CancellationToken ct)
    {
        var models = new List<ModelInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var baseUrl in connections.OllamaBaseUrls.Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            models.AddRange((await FetchOllamaModelsAsync(baseUrl, ct))
                .Where(model => seen.Add($"ollama:{model.Id}")));
        }

        for (var i = 0; i < connections.OpenAiBaseUrls.Count; i++)
        {
            models.AddRange((await FetchOpenAiModelsAsync(
                connections.OpenAiBaseUrls[i],
                connections.OpenAiApiKeys.ElementAtOrDefault(i), ct))
                .Where(model => seen.Add($"openai:{model.Id}")));
        }

        return models;
    }

    private static string ConnectionsFingerprint(ConnectionsConfig c)
    {
        var raw = string.Join('\n', c.OllamaBaseUrls) + '|'
            + string.Join('\n', c.OpenAiBaseUrls) + '|'
            + string.Join('\n', c.OpenAiApiKeys);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    /// <summary>
    /// Lista modelos de uma única conexão cadastrada (por tipo e índice).
    /// Usado pela UI admin para combos de modelos por conexão.
    /// </summary>
    /// <param name="type">"ollama" ou "openai".</param>
    /// <param name="index">Índice da conexão na lista configurada.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<List<ModelInfo>> ListModelsForConnectionAsync(
        string type, int index, CancellationToken ct = default)
    {
        var connections = await config.GetConnectionsAsync(ct);
        return type == "ollama"
            ? index >= 0 && index < connections.OllamaBaseUrls.Count
                ? await FetchOllamaModelsAsync(connections.OllamaBaseUrls[index], ct)
                : []
            : index >= 0 && index < connections.OpenAiBaseUrls.Count
                ? await FetchOpenAiModelsAsync(
                    connections.OpenAiBaseUrls[index],
                    connections.OpenAiApiKeys.ElementAtOrDefault(index), ct)
                : [];
    }

    /// <summary>GET {base}/api/tags do Ollama; vazio quando indisponível.</summary>
    private async Task<List<ModelInfo>> FetchOllamaModelsAsync(
        string baseUrl, CancellationToken ct)
    {
        var models = new List<ModelInfo>();
        try
        {
            using var response = await httpClientFactory.CreateClient()
                .GetAsync($"{TrimSlash(baseUrl)}/api/tags", ct);
            if (!response.IsSuccessStatusCode)
            {
                return models;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(json);
            foreach (var model in node?["models"]?.AsArray() ?? [])
            {
                var id = model?["model"]?.GetValue<string>()
                    ?? model?["name"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id))
                {
                    models.Add(new ModelInfo(id, model!["name"]?.GetValue<string>() ?? id, "ollama", "ollama"));
                }
            }
        }
        catch (HttpRequestException ex)
        {
            // Provedor indisponível: ignora e segue para o próximo.
            logger.LogDebug(ex, "Falha ao listar modelos do Ollama em {BaseUrl}.", baseUrl);
        }
        catch (TaskCanceledException ex)
        {
            // Timeout na conexão: ignora e segue para o próximo.
            logger.LogDebug(ex, "Timeout ao listar modelos do Ollama em {BaseUrl}.", baseUrl);
        }
        return models;
    }

    /// <summary>GET {base}/models de uma API OpenAI-compatível; vazio quando indisponível.</summary>
    private async Task<List<ModelInfo>> FetchOpenAiModelsAsync(
        string baseUrl, string? apiKey, CancellationToken ct)
    {
        var models = new List<ModelInfo>();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return models;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{TrimSlash(baseUrl)}/models");
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await httpClientFactory.CreateClient().SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return models;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(json);
            foreach (var model in node?["data"]?.AsArray() ?? [])
            {
                var id = model?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id))
                {
                    models.Add(new ModelInfo(
                        id,
                        id,
                        "openai",
                        model!["owned_by"]?.GetValue<string>()));
                }
            }
        }
        catch (HttpRequestException ex)
        {
            // Provedor indisponível: ignora e segue para o próximo.
            logger.LogDebug(ex, "Falha ao listar modelos OpenAI em {BaseUrl}.", baseUrl);
        }
        catch (TaskCanceledException ex)
        {
            // Timeout na conexão: ignora e segue para o próximo.
            logger.LogDebug(ex, "Timeout ao listar modelos OpenAI em {BaseUrl}.", baseUrl);
        }
        return models;
    }

    /// <summary>Executa uma completion sem streaming e retorna o texto do assistant.</summary>
    /// <param name="request">Requisição de completion (Stream é ignorado).</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<string> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
    {
        var connections = await config.GetConnectionsAsync(ct);
        var provider = await ResolveProviderAsync(request, connections, ct);

        return provider switch
        {
            "ollama" => await CompleteOllamaAsync(request, connections, ct),
            "openai" => await CompleteOpenAiAsync(request, connections, ct),
            _ => throw new InvalidOperationException(
                $"Nenhuma conexão configurada atende ao modelo '{request.Model}'."),
        };
    }

    /// <summary>
    /// Transmite uma completion como linhas SSE no formato de chunks da OpenAI.
    /// Roteia para Ollama ou OpenAI conforme o provedor resolvido para o modelo.
    /// </summary>
    /// <param name="request">Requisição de completion enviada pelo cliente.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var connections = await config.GetConnectionsAsync(ct);
        var provider = await ResolveProviderAsync(request, connections, ct);

        var lines = provider switch
        {
            "ollama" => StreamOllamaAsync(request, connections, ct),
            "openai" => StreamOpenAiAsync(request, connections, ct),
            _ => throw new InvalidOperationException(
                $"Nenhuma conexão configurada atende ao modelo '{request.Model}'."),
        };

        await foreach (var line in lines.WithCancellation(ct))
        {
            yield return line;
        }
    }

    /// <summary>
    /// Executa uma completion não-streamed que pode retornar tool_calls
    /// (loop de tools). Roteia Ollama/OpenAI e normaliza as chamadas.
    /// </summary>
    /// <param name="request">Requisição com Tools preenchidas.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<ProviderCompletion> CompleteWithToolsAsync(
        ChatCompletionRequest request, CancellationToken ct = default)
    {
        var connections = await config.GetConnectionsAsync(ct);
        var provider = await ResolveProviderAsync(request, connections, ct);
        var payload = BuildPayload(request, stream: false);
        JsonNode? json;
        bool openAi = provider == "openai";

        if (openAi)
        {
            var (index, baseUrl) = FirstOpenAiConnection(connections);
            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post, $"{TrimSlash(baseUrl)}/chat/completions")
            {
                Content = new StringContent(
                    payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            var apiKey = connections.OpenAiApiKeys.ElementAtOrDefault(index);
            if (!string.IsNullOrEmpty(apiKey))
            {
                httpRequest.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
            }
            using var response = await httpClientFactory.CreateClient()
                .SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();
            json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        else
        {
            var baseUrl = FirstBaseUrl(connections)
                ?? throw new InvalidOperationException("Nenhuma URL do Ollama configurada.");
            using var response = await httpClientFactory.CreateClient().PostAsync(
                $"{TrimSlash(baseUrl)}/api/chat",
                new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                ct);
            response.EnsureSuccessStatusCode();
            json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        }

        return ParseCompletionMessage(json, openAi);
    }

    /// <summary>Normaliza <c>choices[0].message</c> (OpenAI) ou <c>message</c> (Ollama).</summary>
    private static ProviderCompletion ParseCompletionMessage(JsonNode? json, bool openAi)
    {
        var firstChoice = json?["choices"] is JsonArray { Count: > 0 } arr ? arr[0] : null;
        var message = openAi ? firstChoice?["message"] : json?["message"];
        var content = message?["content"]?.GetValue<string>() ?? string.Empty;
        var calls = new List<ProviderToolCall>();
        var callsNode = message?["tool_calls"]?.AsArray();
        var callsJson = callsNode?.ToJsonString() ?? "[]";
        if (callsNode is not null)
        {
            foreach (var call in callsNode)
            {
                var name = call?["function"]?["name"]?.GetValue<string>();
                if (name is null)
                {
                    continue;
                }
                var args = call?["function"]?["arguments"];
                var argsJson = args is JsonValue ? args.GetValue<string>() : args?.ToJsonString() ?? "{}";
                calls.Add(new ProviderToolCall(
                    call?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                    name, argsJson));
            }
        }

        return new ProviderCompletion(content, calls, callsJson);
    }

    /// <summary>
    /// Variante streamed de <see cref="CompleteWithToolsAsync"/>: emite os
    /// pedaços de texto via <paramref name="onDelta"/> conforme chegam e
    /// acumula os fragmentos de <c>tool_calls</c> (OpenAI manda name/id uma
    /// vez e arguments em pedaços por <c>index</c>; Ollama manda o array
    /// completo na mensagem final). Retorna null quando a requisição falha
    /// antes de produzir saída — sinal para o chamador cair na variante
    /// buffered (alguns endpoints OpenAI-compatíveis rejeitam stream+tools).
    /// </summary>
    /// <param name="request">Requisição com Tools preenchidas (ou null para o round final).</param>
    /// <param name="onDelta">Callback por pedaço de texto (pode ser null — vira buffered).</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<ProviderCompletion?> CompleteWithToolsStreamingAsync(
        ChatCompletionRequest request,
        Func<string, CancellationToken, Task>? onDelta,
        CancellationToken ct = default)
    {
        var connections = await config.GetConnectionsAsync(ct);
        var provider = await ResolveProviderAsync(request, connections, ct);
        var payload = BuildPayload(request, stream: true);

        try
        {
            return provider == "openai"
                ? await StreamOpenAiToolsAsync(request, connections, payload, onDelta, ct)
                : await StreamOllamaToolsAsync(request, connections, payload, onDelta, ct);
        }
        catch (HttpRequestException)
        {
            // Falha de transporte/setup antes da resposta — o chamador
            // refaz a rodada buffered. Erros mid-stream chegam aqui também,
            // mas o texto já emitido é reenviado inteiro no fallback: melhor
            // duplicar do que perder a rodada.
            return null;
        }
    }

    /// <summary>
    /// Round streamed OpenAI: lê linhas <c>data:</c>, emite deltas de
    /// <c>content</c> e monta os tool_calls fragmentados por
    /// <c>index</c>. <c>ToolCallsJson</c> reproduz o formato upstream
    /// (<c>arguments</c> como string) para o echo na próxima mensagem.
    /// </summary>
    private async Task<ProviderCompletion> StreamOpenAiToolsAsync(
        ChatCompletionRequest request,
        ConnectionsConfig connections,
        JsonObject payload,
        Func<string, CancellationToken, Task>? onDelta,
        CancellationToken ct)
    {
        var (index, baseUrl) = FirstOpenAiConnection(connections);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{TrimSlash(baseUrl)}/chat/completions")
        {
            Content = new StringContent(
                payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var apiKey = connections.OpenAiApiKeys.ElementAtOrDefault(index);
        if (!string.IsNullOrEmpty(apiKey))
        {
            httpRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        if (!string.Equals(response.Content.Headers.ContentType?.MediaType,
                "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            // Endpoint ignorou stream:true (compat/proxy) e devolveu a
            // completion de uma vez — parseia buffered e entrega o texto
            // num delta só, mantendo a semântica do método.
            var buffered = ParseCompletionMessage(
                JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)), openAi: true);
            if (buffered.Content.Length > 0 && onDelta is not null)
            {
                await onDelta(buffered.Content, ct);
            }
            return buffered;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var content = new StringBuilder();
        var callBuffers = new SortedDictionary<int, OpenAiToolCallBuffer>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
            {
                break;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }

            var choice = node?["choices"] is JsonArray { Count: > 0 } arr ? arr[0] : null;
            var delta = choice?["delta"];
            var piece = delta?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(piece))
            {
                content.Append(piece);
                if (onDelta is not null)
                {
                    await onDelta(piece, ct);
                }
            }

            if (delta?["tool_calls"] is not JsonArray callParts)
            {
                continue;
            }

            foreach (var part in callParts)
            {
                var position = part?["index"]?.GetValue<int>() ?? callBuffers.Count;
                if (!callBuffers.TryGetValue(position, out var buffer))
                {
                    buffer = new OpenAiToolCallBuffer();
                    callBuffers[position] = buffer;
                }
                buffer.Id = part?["id"]?.GetValue<string>() ?? buffer.Id;
                var name = part?["function"]?["name"]?.GetValue<string>();
                if (name is not null)
                {
                    buffer.Name = name;
                }
                var args = part?["function"]?["arguments"]?.GetValue<string>();
                if (args is not null)
                {
                    buffer.Args.Append(args);
                }
            }
        }

        var calls = callBuffers.Values
            .Where(b => b.Name is not null)
            .Select(b => new ProviderToolCall(
                b.Id ?? Guid.NewGuid().ToString("N"),
                b.Name!,
                b.Args.Length > 0 ? b.Args.ToString() : "{}"))
            .ToList();
        var callsJson = JsonSerializer.Serialize(calls.Select(c => new
        {
            id = c.Id,
            type = "function",
            function = new { name = c.Name, arguments = c.ArgumentsJson },
        }));
        return new ProviderCompletion(content.ToString(), calls, callsJson);
    }

    /// <summary>Buffer de uma tool_call fragmentada no stream OpenAI.</summary>
    private sealed class OpenAiToolCallBuffer
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Args { get; } = new();
    }

    /// <summary>
    /// Round streamed Ollama (<c>/api/chat</c> NDJSON): emite
    /// <c>message.content</c> por linha e captura o array
    /// <c>message.tool_calls</c> verbatim — o echo na próxima mensagem
    /// precisa do formato original (arguments como objeto).
    /// </summary>
    private async Task<ProviderCompletion> StreamOllamaToolsAsync(
        ChatCompletionRequest request,
        ConnectionsConfig connections,
        JsonObject payload,
        Func<string, CancellationToken, Task>? onDelta,
        CancellationToken ct)
    {
        var baseUrl = FirstBaseUrl(connections)
            ?? throw new InvalidOperationException("Nenhuma URL do Ollama configurada.");

        // Ollama aceita "options" em vez de parâmetros de topo (mesmo remap de StreamOllamaAsync).
        if (request.Params is { Count: > 0 })
        {
            var options = new JsonObject();
            foreach (var key in new[] { "temperature", "top_p", "top_k", "num_predict", "repeat_penalty", "seed", "stop" })
            {
                if (payload.Remove(key, out var value))
                {
                    options[MapOllamaParam(key)] = value;
                }
            }

            if (options.Count > 0)
            {
                payload["options"] = options;
            }
        }

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{TrimSlash(baseUrl)}/api/chat")
        {
            Content = new StringContent(
                payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var content = new StringBuilder();
        var callNodes = new List<JsonNode>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            var piece = node?["message"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(piece))
            {
                content.Append(piece);
                if (onDelta is not null)
                {
                    await onDelta(piece, ct);
                }
            }

            if (node?["message"]?["tool_calls"] is JsonArray { Count: > 0 } calls)
            {
                callNodes.AddRange(calls.Select(c => c!.DeepClone()));
            }
        }

        var callsJson = new JsonArray(callNodes.ToArray());
        var parsed = new List<ProviderToolCall>();
        foreach (var call in callNodes)
        {
            var name = call?["function"]?["name"]?.GetValue<string>();
            if (name is null)
            {
                continue;
            }
            var args = call["function"]?["arguments"];
            var argsJson = args is JsonValue value && value.TryGetValue<string>(out var s)
                ? s
                : args?.ToJsonString() ?? "{}";
            parsed.Add(new ProviderToolCall(
                call["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                name, argsJson));
        }

        return new ProviderCompletion(content.ToString(), parsed, callsJson.ToJsonString());
    }

    private async Task<string> ResolveProviderAsync(
        ChatCompletionRequest request, ConnectionsConfig connections, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.Connection))
        {
            return request.Connection;
        }

        var ollamaModels = (await ListModelsAsync(ct))
            .Where(m => m.Provider == "ollama")
            .Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ollamaModels.Contains(request.Model))
        {
            return "ollama";
        }

        return connections.OpenAiBaseUrls.Count > 0 ? "openai" : "ollama";
    }

    private JsonObject BuildPayload(ChatCompletionRequest request, bool stream)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray(request.Messages
                .Select(m =>
                {
                    var node = new JsonObject
                    {
                        ["role"] = m.Role,
                        ["content"] = m.Content,
                    };
                    if (m.ToolCallId is not null)
                    {
                        node["tool_call_id"] = m.ToolCallId;
                    }
                    if (m.ToolCallsJson is not null)
                    {
                        node["tool_calls"] = JsonNode.Parse(m.ToolCallsJson);
                    }
                    return (JsonNode)node;
                })
                .ToArray()),
            ["stream"] = stream,
        };

        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = new JsonArray(request.Tools
                .Select(t => JsonNode.Parse(t.GetRawText()))
                .ToArray());
        }

        if (request.Params is not null)
        {
            foreach (var (key, value) in request.Params)
            {
                payload[key] = value switch
                {
                    JsonElement e => JsonNode.Parse(e.GetRawText()),
                    bool b => b,
                    int i => i,
                    long l => l,
                    float f => f,
                    double d => d,
                    decimal m => m,
                    string s => s,
                    null => null,
                    _ => JsonSerializer.SerializeToNode(value, JsonOptions),
                };
            }
        }

        return payload;
    }

    private static string? FirstBaseUrl(ConnectionsConfig connections) =>
        connections.OllamaBaseUrls.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));

    private async Task<string> CompleteOllamaAsync(
        ChatCompletionRequest request, ConnectionsConfig connections, CancellationToken ct)
    {
        var baseUrl = FirstBaseUrl(connections)
            ?? throw new InvalidOperationException("Nenhuma URL do Ollama configurada.");

        var payload = BuildPayload(request, stream: false);
        using var response = await httpClientFactory.CreateClient().PostAsync(
            $"{TrimSlash(baseUrl)}/api/chat",
            new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            ct);
        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return json?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
    }

    private async Task<string> CompleteOpenAiAsync(
        ChatCompletionRequest request, ConnectionsConfig connections, CancellationToken ct)
    {
        var (index, baseUrl) = FirstOpenAiConnection(connections);

        var payload = BuildPayload(request, stream: false);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{TrimSlash(baseUrl)}/chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var apiKey = connections.OpenAiApiKeys.ElementAtOrDefault(index);
        if (!string.IsNullOrEmpty(apiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await httpClientFactory.CreateClient().SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return json?["choices"] is JsonArray { Count: > 0 } arr
            ? arr[0]?["message"]?["content"]?.GetValue<string>() ?? string.Empty
            : string.Empty;
    }

    private async IAsyncEnumerable<string> StreamOllamaAsync(
        ChatCompletionRequest request,
        ConnectionsConfig connections,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var baseUrl = FirstBaseUrl(connections)
            ?? throw new InvalidOperationException("Nenhuma URL do Ollama configurada.");

        var payload = BuildPayload(request, stream: true);
        // Ollama aceita "options" em vez de parâmetros de topo: move extras para lá.
        if (request.Params is { Count: > 0 })
        {
            var options = new JsonObject();
            foreach (var key in new[] { "temperature", "top_p", "top_k", "num_predict", "repeat_penalty", "seed", "stop" }
                .Where(payload.ContainsKey))
            {
                payload.Remove(key, out var value);
                options[MapOllamaParam(key)] = value;
            }

            if (options.Count > 0)
            {
                payload["options"] = options;
            }
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{TrimSlash(baseUrl)}/api/chat")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            var done = node?["done"]?.GetValue<bool>() ?? false;
            var content = node?["message"]?["content"]?.GetValue<string>();
            var chunk = new JsonObject
            {
                ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
                ["object"] = "chat.completion.chunk",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = request.Model,
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = content ?? string.Empty,
                    },
                    ["finish_reason"] = done ? "stop" : null,
                }),
            };

            yield return $"data: {chunk.ToJsonString()}";

            if (done)
            {
                yield return "data: [DONE]";
                yield break;
            }
        }

        yield return "data: [DONE]";
    }

    private static string MapOllamaParam(string key) => key switch
    {
        "max_tokens" => "num_predict",
        _ => key,
    };

    private async IAsyncEnumerable<string> StreamOpenAiAsync(
        ChatCompletionRequest request,
        ConnectionsConfig connections,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var (index, baseUrl) = FirstOpenAiConnection(connections);

        var payload = BuildPayload(request, stream: true);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{TrimSlash(baseUrl)}/chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        var apiKey = connections.OpenAiApiKeys.ElementAtOrDefault(index);
        if (!string.IsNullOrEmpty(apiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return line;
            if (line == "data: [DONE]")
            {
                yield break;
            }
        }
    }

    private static (int Index, string BaseUrl) FirstOpenAiConnection(ConnectionsConfig connections)
    {
        for (var i = 0; i < connections.OpenAiBaseUrls.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(connections.OpenAiBaseUrls[i]))
            {
                return (i, connections.OpenAiBaseUrls[i]);
            }
        }

        throw new InvalidOperationException("Nenhuma conexão OpenAI configurada.");
    }

    private static string TrimSlash(string url) => url.TrimEnd('/');
}

/// <summary>Resultado de uma completion com suporte a tool_calls.</summary>
/// <param name="Content">Texto final (pode ser vazio quando só há calls).</param>
/// <param name="ToolCalls">Chamadas de função pedidas pelo modelo.</param>
public sealed record ProviderCompletion(
    string Content,
    IReadOnlyList<ProviderToolCall> ToolCalls,
    string ToolCallsJson);

/// <summary>Chamada de função normalizada (Ollama/OpenAI).</summary>
/// <param name="Id">Id da chamada (echo em tool_call_id).</param>
/// <param name="Name">Nome da função.</param>
/// <param name="ArgumentsJson">Argumentos em JSON.</param>
public sealed record ProviderToolCall(string Id, string Name, string ArgumentsJson);

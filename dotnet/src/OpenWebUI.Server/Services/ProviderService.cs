using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenWebUI.Shared.Contracts;

namespace OpenWebUI.Server.Services;

/// <summary>
/// Resolve conexões com provedores de IA (Ollama e APIs compatíveis com OpenAI),
/// lista modelos e transmite completions no formato SSE da OpenAI.
/// </summary>
public class ProviderService(IHttpClientFactory httpClientFactory, ConfigService config)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Lista modelos de todas as conexões configuradas.</summary>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var connections = await config.GetConnectionsAsync(ct);
        var models = new List<ModelInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var baseUrl in connections.OllamaBaseUrls.Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            try
            {
                using var response = await httpClientFactory.CreateClient()
                    .GetAsync($"{TrimSlash(baseUrl)}/api/tags", ct);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                var node = JsonNode.Parse(json);
                foreach (var model in node?["models"]?.AsArray() ?? [])
                {
                    var id = model?["model"]?.GetValue<string>()
                        ?? model?["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(id) && seen.Add($"ollama:{id}"))
                    {
                        models.Add(new ModelInfo(id, model?["name"]?.GetValue<string>() ?? id, "ollama", "ollama"));
                    }
                }
            }
            catch (HttpRequestException)
            {
                // Provedor indisponível: ignora e segue para o próximo.
            }
            catch (TaskCanceledException)
            {
                // Timeout na conexão: ignora e segue para o próximo.
            }
        }

        for (var i = 0; i < connections.OpenAiBaseUrls.Count; i++)
        {
            var baseUrl = connections.OpenAiBaseUrls[i];
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                continue;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{TrimSlash(baseUrl)}/models");
                var apiKey = connections.OpenAiApiKeys.ElementAtOrDefault(i);
                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                using var response = await httpClientFactory.CreateClient().SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                var node = JsonNode.Parse(json);
                foreach (var model in node?["data"]?.AsArray() ?? [])
                {
                    var id = model?["id"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(id) && seen.Add($"openai:{id}"))
                    {
                        models.Add(new ModelInfo(
                            id,
                            id,
                            "openai",
                            model?["owned_by"]?.GetValue<string>()));
                    }
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }
        }

        return models;
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

    private async IAsyncEnumerable<string> StreamOllamaAsync(
        ChatCompletionRequest request,
        ConnectionsConfig connections,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var baseUrl = connections.OllamaBaseUrls.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u))
            ?? throw new InvalidOperationException("Nenhuma URL do Ollama configurada.");

        var payload = new
        {
            model = request.Model,
            messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }),
            stream = true,
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{TrimSlash(baseUrl)}/api/chat")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
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

    private async IAsyncEnumerable<string> StreamOpenAiAsync(
        ChatCompletionRequest request,
        ConnectionsConfig connections,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var index = -1;
        for (var i = 0; i < connections.OpenAiBaseUrls.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(connections.OpenAiBaseUrls[i]))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            throw new InvalidOperationException("Nenhuma conexão OpenAI configurada.");
        }

        var payload = new
        {
            model = request.Model,
            messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }),
            stream = true,
        };

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{TrimSlash(connections.OpenAiBaseUrls[index])}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
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

    private static string TrimSlash(string url) => url.TrimEnd('/');
}

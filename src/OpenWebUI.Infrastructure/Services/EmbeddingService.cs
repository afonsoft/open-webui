using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Serviço de embeddings: chama Ollama (/api/embed) ou API compatível com
/// OpenAI (/v1/embeddings) usando as conexões configuradas. Sem provider
/// configurado ou em falha, retorna null (RAG fica desabilitado e o
/// comportamento de injeção de texto integral é preservado).
/// </summary>
public class EmbeddingService(IHttpClientFactory httpClientFactory, ConfigService config)
{
    private const string OllamaModelEnv = "RAG_EMBEDDING_MODEL";
    private const string OpenAiModelEnv = "RAG_EMBEDDING_MODEL_OPENAI";

    /// <summary>Gera o embedding de um texto ou null se nenhum provider atende.</summary>
    /// <param name="text">Texto a embeddar.</param>
    /// <param name="ct">Cancelamento.</param>
    public virtual async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        var connections = await config.GetConnectionsAsync(ct);
        var http = httpClientFactory.CreateClient("ai-providers");
        http.Timeout = TimeSpan.FromSeconds(30);

        foreach (var baseUrl in connections.OllamaBaseUrls)
        {
            try
            {
                var model = Environment.GetEnvironmentVariable(OllamaModelEnv)
                    ?? "nomic-embed-text";
                var response = await http.PostAsJsonAsync(
                    $"{baseUrl.TrimEnd('/')}/api/embed",
                    new { model, input = text }, ct);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }
                var payload = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(ct);
                var vector = payload?.Embeddings?.FirstOrDefault();
                if (vector is { Length: > 0 })
                {
                    return vector;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Connection falhou — tenta a próxima base URL.
            }
        }

        var openAiModel = await ResolveOpenAiModelAsync(ct);
        for (var i = 0; i < connections.OpenAiBaseUrls.Count; i++)
        {
            try
            {
                var baseUrl = connections.OpenAiBaseUrls[i].TrimEnd('/');
                var model = openAiModel;
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, $"{baseUrl}/embeddings");
                if (i < connections.OpenAiApiKeys.Count &&
                    !string.IsNullOrEmpty(connections.OpenAiApiKeys[i]))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue(
                            "Bearer", connections.OpenAiApiKeys[i]);
                }
                request.Content = JsonContent.Create(new { model, input = text });
                var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }
                var payload = await response.Content.ReadFromJsonAsync<OpenAiEmbedResponse>(ct);
                var vector = payload?.Data?.FirstOrDefault()?.Embedding;
                if (vector is { Length: > 0 })
                {
                    return vector;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Connection falhou — tenta a próxima base URL.
            }
        }

        return null;
    }

    /// <summary>
    /// Gera embeddings de vários textos em uma única requisição por provider
    /// (Ollama /api/embed e OpenAI /embeddings aceitam input como array).
    /// Retorna null quando nenhum provider atende o batch — o chamador pode
    /// cair no modo sequencial por EmbedAsync.
    /// </summary>
    /// <param name="texts">Textos a embeddar (ordem preservada).</param>
    /// <param name="ct">Cancelamento.</param>
    public virtual async Task<float[][]?> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        ConnectionsConfig connections;
        HttpClient http;
        try
        {
            connections = await config.GetConnectionsAsync(ct);
            http = httpClientFactory.CreateClient("ai-providers");
            http.Timeout = TimeSpan.FromSeconds(60);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        foreach (var baseUrl in connections.OllamaBaseUrls)
        {
            try
            {
                var model = Environment.GetEnvironmentVariable(OllamaModelEnv)
                    ?? "nomic-embed-text";
                var response = await http.PostAsJsonAsync(
                    $"{baseUrl.TrimEnd('/')}/api/embed",
                    new { model, input = texts }, ct);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }
                var payload = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(ct);
                var vectors = payload?.Embeddings;
                if (vectors is { Length: > 0 })
                {
                    return vectors;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Connection falhou — tenta a próxima base URL.
            }
        }

        var openAiModel = await ResolveOpenAiModelAsync(ct);
        for (var i = 0; i < connections.OpenAiBaseUrls.Count; i++)
        {
            try
            {
                var baseUrl = connections.OpenAiBaseUrls[i].TrimEnd('/');
                var model = openAiModel;
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, $"{baseUrl}/embeddings");
                if (i < connections.OpenAiApiKeys.Count &&
                    !string.IsNullOrEmpty(connections.OpenAiApiKeys[i]))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue(
                            "Bearer", connections.OpenAiApiKeys[i]);
                }
                request.Content = JsonContent.Create(new { model, input = texts });
                var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }
                var payload = await response.Content.ReadFromJsonAsync<OpenAiEmbedResponse>(ct);
                var vectors = payload?.Data?.Select(d => d.Embedding)
                    .Where(e => e is { Length: > 0 })
                    .Select(e => e!)
                    .ToArray();
                if (vectors is { Length: > 0 } && vectors.Length == texts.Count)
                {
                    return vectors;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Connection falhou — tenta a próxima base URL.
            }
        }

        return null;
    }

    /// <summary>
    /// Modelo de embedding OpenAI: env <c>RAG_EMBEDDING_MODEL_OPENAI</c> →
    /// kv <c>rag.embedding.model</c> (preenchido pela detecção de
    /// capacidades) → <c>text-embedding-3-small</c>.
    /// </summary>
    private async Task<string> ResolveOpenAiModelAsync(CancellationToken ct) =>
        Environment.GetEnvironmentVariable(OpenAiModelEnv)
        ?? await config.GetAsync<string?>("rag.embedding.model", null, ct)
        ?? "text-embedding-3-small";

    private sealed record OllamaEmbedResponse(
        [property: JsonPropertyName("embeddings")] float[][]? Embeddings);

    private sealed record OpenAiEmbedResponse(
        [property: JsonPropertyName("data")] OpenAiEmbedItem[]? Data);

    private sealed record OpenAiEmbedItem(
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Consome o endpoint SSE <c>/api/chat/completions</c> e produz deltas de conteúdo.
/// </summary>
public class ChatStreamService(HttpClient http, AuthService auth)
{
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
        using var httpRequest = auth.CreateRequest(HttpMethod.Post, "/api/chat/completions");
        httpRequest.SetBrowserResponseStreamingEnabled(true);
        httpRequest.Content = JsonContent.Create(request);

        using var response = await http.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Erro {(int)response.StatusCode} ao gerar resposta: {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
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
                delta = node?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
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
}

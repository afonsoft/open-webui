using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Infraestrutura comum dos motores de mídia (imagem e vídeo): HTTP com
/// Bearer, timeout por config, leitura de <c>EngineParams</c> e parsing
/// de tamanho <c>WxH</c>. A configuração transportada é
/// <see cref="ImagesConfig"/> — motores de vídeo recebem a projeção
/// equivalente de <c>VideoConfig</c>.
/// </summary>
public abstract class MediaEngineBase(IHttpClientFactory httpClientFactory)
{
    /// <summary>Cliente HTTP nomeado do motor.</summary>
    protected HttpClient Client => httpClientFactory.CreateClient("images");

    /// <summary>POST JSON com Bearer e timeout da config.</summary>
    protected async Task<JsonNode?> PostJsonAsync(
        ImagesConfig config, string url, JsonObject payload, CancellationToken ct, string? apiKeyHeader = null)
    {
        using var timeout = Timeout(config, ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyAuth(request, config, apiKeyHeader);
        using var response = await Client.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>GET JSON com Bearer e timeout da config.</summary>
    protected async Task<JsonNode?> GetJsonAsync(ImagesConfig config, string url, CancellationToken ct)
    {
        using var timeout = Timeout(config, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request, config, null);
        using var response = await Client.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>GET de bytes (download do arquivo gerado).</summary>
    protected async Task<byte[]> GetBytesAsync(string url, ImagesConfig config, CancellationToken ct)
    {
        using var timeout = Timeout(config, ct);
        return await Client.GetByteArrayAsync(url, timeout.Token);
    }

    /// <summary>Aplica Bearer (ou header customizado) quando a chave existe.</summary>
    protected static void ApplyAuth(HttpRequestMessage request, ImagesConfig config, string? apiKeyHeader)
    {
        if (string.IsNullOrEmpty(config.ApiKey))
        {
            return;
        }
        if (apiKeyHeader is null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }
        else
        {
            request.Headers.TryAddWithoutValidation(apiKeyHeader, config.ApiKey);
        }
    }

    /// <summary>CTS limitado pelo timeout da config (5–600s).</summary>
    protected static CancellationTokenSource Timeout(ImagesConfig config, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 5, 600)));
        return cts;
    }

    /// <summary>Lê um parâmetro livre do <c>EngineParams</c> (JSON) da config.</summary>
    protected static string? Param(ImagesConfig config, string key)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(config.EngineParams) ? "{}" : config.EngineParams);
            return node?[key]?.GetValue<string>();
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }

    /// <summary>Converte "WxH" em (width, height); fallback para o valor informado.</summary>
    protected static (int W, int H) ParseSize(string? size, int fallback = 1024)
    {
        var s = size?.Trim() ?? "";
        var parts = s.Split('x', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)
            ? (w, h)
            : (fallback, fallback);
    }

    /// <summary>Base URL sem barra final.</summary>
    protected static string Base(ImagesConfig config) => config.BaseUrl.TrimEnd('/');
}

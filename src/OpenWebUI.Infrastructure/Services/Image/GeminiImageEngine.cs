using System.Text.Json;
using System.Text.Json.Nodes;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services.Image;

/// <summary>Motor Gemini/Imagen (POST /v1beta/models/{model}:predict, key via querystring).</summary>
public sealed class GeminiImageEngine(IHttpClientFactory httpClientFactory) : ImageEngineBase(httpClientFactory)
{
    /// <inheritdoc />
    public override string Name => "gemini";

    /// <inheritdoc />
    public override async Task<IReadOnlyList<byte[]>> GenerateAsync(
        ImagesConfig config, string prompt, int n, string? size, CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(config.Model) ? "imagen-3.0-generate-002" : config.Model;
        var payload = new JsonObject
        {
            ["instances"] = new JsonArray(new JsonObject { ["prompt"] = prompt }),
            ["parameters"] = new JsonObject
            {
                ["sampleCount"] = n,
                ["aspectRatio"] = AspectRatio(string.IsNullOrWhiteSpace(size) ? config.Size : size),
            },
        };
        var url = $"{Base(config)}/v1beta/models/{model}:predict";
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            url += $"?key={Uri.EscapeDataString(config.ApiKey)}";
        }

        var json = await PostJsonAsync(config, url, payload, ct);
        var images = new List<byte[]>();
        foreach (var prediction in json?["predictions"]?.AsArray() ?? [])
        {
            var b64 = prediction?["bytesBase64Encoded"]?.GetValue<string>() ?? prediction?.GetValue<string>();
            if (!string.IsNullOrEmpty(b64))
            {
                images.Add(Convert.FromBase64String(b64));
            }
        }
        return images;
    }

    /// <inheritdoc />
    public override async Task<(bool Ok, string Detail)> TestAsync(ImagesConfig config, CancellationToken ct)
    {
        try
        {
            var model = string.IsNullOrWhiteSpace(config.Model) ? "imagen-3.0-generate-002" : config.Model;
            var url = $"{Base(config)}/v1beta/models/{model}";
            if (!string.IsNullOrEmpty(config.ApiKey))
            {
                url += $"?key={Uri.EscapeDataString(config.ApiKey)}";
            }
            await GetJsonAsync(config, url, ct);
            return (true, "ok");
        }
        catch (HttpRequestException ex) { return (false, ex.Message); }
        catch (JsonException ex) { return (false, ex.Message); }
        catch (InvalidOperationException ex) { return (false, ex.Message); }
        catch (TaskCanceledException ex) { return (false, ex.Message); }
    }

    private static string AspectRatio(string size)
    {
        var (w, h) = ParseSize(size, 1024);
        if (w == h) return "1:1";
        return w > h ? "16:9" : "9:16";
    }
}

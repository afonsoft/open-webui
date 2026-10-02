using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services.Image;

/// <summary>Motor OpenAI-compatible (POST /images/generations; edits via /images/edits).</summary>
public sealed class OpenAiImageEngine(IHttpClientFactory httpClientFactory) : ImageEngineBase(httpClientFactory)
{
    /// <inheritdoc />
    public override string Name => "openai";

    /// <inheritdoc />
    public override bool SupportsEdit => true;

    /// <inheritdoc />
    public override async Task<IReadOnlyList<byte[]>> GenerateAsync(
        ImagesConfig config, string prompt, int n, string? size, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = config.Model,
            ["prompt"] = prompt,
            ["n"] = n,
            ["size"] = string.IsNullOrWhiteSpace(size) ? config.Size : size,
        };
        if (config.Model.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase))
        {
            payload["response_format"] = "b64_json";
        }

        var json = await PostJsonAsync(config, $"{Base(config)}/images/generations", payload, ct);
        var data = json?["data"]?.AsArray()
            ?? throw new InvalidOperationException("Resposta do provedor sem imagens.");

        var images = new List<byte[]>();
        foreach (var item in data)
        {
            var bytes = await ResolveBytesAsync(item, config, ct);
            if (bytes is not null)
            {
                images.Add(bytes);
            }
        }
        return images;
    }

    /// <inheritdoc />
    public override async Task<byte[]> EditAsync(
        ImagesConfig config, byte[] sourceImage, string prompt, string? size, CancellationToken ct)
    {
        using var timeout = Timeout(config, ct);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(sourceImage), "image", "image.png");
        form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent(config.Model.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase)
            ? config.Model : "dall-e-2"), "model");
        form.Add(new StringContent(string.IsNullOrWhiteSpace(size) ? config.Size : size), "size");
        form.Add(new StringContent("b64_json"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base(config)}/images/edits")
        {
            Content = form,
        };
        ApplyAuth(request, config, null);
        using var response = await Client.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var bytes = await ResolveBytesAsync(json?["data"]?.AsArray()?[0], config, ct);
        return bytes ?? throw new InvalidOperationException("Resposta de edição sem imagem.");
    }

    /// <inheritdoc />
    public override async Task<(bool Ok, string Detail)> TestAsync(ImagesConfig config, CancellationToken ct)
    {
        try
        {
            await GetJsonAsync(config, $"{Base(config)}/models", ct);
            return (true, "ok");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<byte[]?> ResolveBytesAsync(JsonNode? item, ImagesConfig config, CancellationToken ct)
    {
        var b64 = item?["b64_json"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(b64))
        {
            return Convert.FromBase64String(b64);
        }
        var url = item?["url"]?.GetValue<string>();
        return string.IsNullOrEmpty(url) ? null : await GetBytesAsync(url, config, ct);
    }
}

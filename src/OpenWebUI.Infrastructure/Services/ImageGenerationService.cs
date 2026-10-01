using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Gera imagens a partir de um prompt chamando um provedor compatível com a
/// API OpenAI Images (<c>POST /images/generations</c>) e persiste os binários
/// como arquivos do usuário (<see cref="FileEntry"/>).
/// </summary>
public class ImageGenerationService(IHttpClientFactory httpClientFactory, ConfigService config, AppDbContext db)
{
    /// <summary>Configuração ativa de geração de imagens.</summary>
    /// <param name="ct">Token de cancelamento.</param>
    public Task<ImagesConfig> GetConfigAsync(CancellationToken ct = default) =>
        config.GetAsync("images.config", ImagesConfig.Default, ct);

    /// <summary>Persiste a configuração de geração de imagens.</summary>
    /// <param name="images">Configuração a gravar.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public Task SetConfigAsync(ImagesConfig images, CancellationToken ct = default) =>
        config.SetAsync("images.config", images, ct);

    /// <summary>
    /// Gera imagens com o provedor configurado e grava os arquivos em disco.
    /// </summary>
    /// <param name="prompt">Descrição da imagem.</param>
    /// <param name="n">Quantidade desejada (limitada entre 1 e 4).</param>
    /// <param name="size">Tamanho opcional; quando nulo usa o configurado.</param>
    /// <param name="userId">Dono dos arquivos gerados.</param>
    /// <param name="uploadDir">Diretório onde os binários são gravados.</param>
    /// <param name="ct">Token de cancelamento.</param>
    /// <returns>Arquivos criados para cada imagem gerada.</returns>
    /// <exception cref="InvalidOperationException">Feature desabilitada ou sem URL configurada.</exception>
    public async Task<List<FileEntry>> GenerateAsync(
        string prompt, int n, string? size, string userId, string uploadDir, CancellationToken ct = default)
    {
        var images = await GetConfigAsync(ct);
        if (!images.Enabled || string.IsNullOrWhiteSpace(images.BaseUrl))
        {
            throw new InvalidOperationException("Geração de imagens desabilitada.");
        }

        n = Math.Clamp(n, 1, 4);
        var payload = new JsonObject
        {
            ["model"] = images.Model,
            ["prompt"] = prompt,
            ["n"] = n,
            ["size"] = string.IsNullOrWhiteSpace(size) ? images.Size : size,
        };
        // dall-e aceita response_format; gpt-image-1 sempre retorna b64_json.
        if (images.Model.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase))
        {
            payload["response_format"] = "b64_json";
        }

        var url = $"{images.BaseUrl.TrimEnd('/')}/images/generations";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(images.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", images.ApiKey);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(images.TimeoutSeconds, 5, 600)));
        using var response = await httpClientFactory.CreateClient().SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var data = json?["data"]?.AsArray()
            ?? throw new InvalidOperationException("Resposta do provedor sem imagens.");

        Directory.CreateDirectory(uploadDir);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var files = new List<FileEntry>();
        foreach (var item in data)
        {
            var bytes = await ResolveImageBytesAsync(item, timeout.Token);
            if (bytes is null)
            {
                continue;
            }

            var id = Guid.NewGuid().ToString();
            var filename = $"generated-{id[..8]}.png";
            var storagePath = Path.Combine(uploadDir, $"{id}_{filename}");
            await File.WriteAllBytesAsync(storagePath, bytes, ct);

            var entry = new FileEntry
            {
                Id = id,
                UserId = userId,
                Filename = filename,
                ContentType = "image/png",
                StoragePath = storagePath,
                Size = bytes.Length,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Files.Add(entry);
            files.Add(entry);
        }

        await db.SaveChangesAsync(ct);
        return files;
    }

    /// <summary>Extrai os bytes da imagem do item de resposta (b64_json ou URL).</summary>
    private async Task<byte[]?> ResolveImageBytesAsync(JsonNode? item, CancellationToken ct)
    {
        var b64 = item?["b64_json"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(b64))
        {
            return Convert.FromBase64String(b64);
        }

        var imageUrl = item?["url"]?.GetValue<string>();
        return string.IsNullOrEmpty(imageUrl)
            ? null
            : await httpClientFactory.CreateClient().GetByteArrayAsync(imageUrl, ct);
    }
}

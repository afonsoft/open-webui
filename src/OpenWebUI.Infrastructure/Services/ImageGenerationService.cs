using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services.Image;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Orquestra a geração/edição de imagens delegando ao motor configurado
/// (<see cref="ImageEngineFactory"/>) e persiste os binários como arquivos
/// do usuário (<see cref="FileEntry"/>).
/// </summary>
public class ImageGenerationService(
    ImageEngineFactory engineFactory, ConfigService config, AppDbContext db)
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

    /// <summary>Gera imagens com o motor configurado e grava os arquivos em disco.</summary>
    /// <param name="prompt">Descrição da imagem.</param>
    /// <param name="n">Quantidade desejada (limitada entre 1 e 4).</param>
    /// <param name="size">Tamanho opcional; quando nulo usa o configurado.</param>
    /// <param name="userId">Dono dos arquivos gerados.</param>
    /// <param name="storage">Storage de arquivos (local ou S3).</param>
    /// <param name="ct">Token de cancelamento.</param>
    /// <returns>Arquivos criados para cada imagem gerada.</returns>
    /// <exception cref="InvalidOperationException">Feature desabilitada ou motor inválido.</exception>
    public async Task<List<FileEntry>> GenerateAsync(
        string prompt, int n, string? size, string userId, IFileStorage storage,
        CancellationToken ct = default)
    {
        var images = await GetConfigAsync(ct);
        if (!images.Enabled || string.IsNullOrWhiteSpace(images.BaseUrl))
        {
            throw new InvalidOperationException("Geração de imagens desabilitada.");
        }

        var engine = engineFactory.Resolve(images.Engine);
        var bytes = await engine.GenerateAsync(images, prompt, Math.Clamp(n, 1, 4), size, ct);
        return await PersistAsync(bytes, userId, storage, ct);
    }

    /// <summary>Edita uma imagem existente do usuário (img2img/edits).</summary>
    /// <param name="imageId">Id do arquivo de origem (image/*) pertencente ao usuário.</param>
    /// <param name="prompt">Instrução de edição.</param>
    /// <param name="size">Tamanho opcional.</param>
    /// <param name="userId">Dono do arquivo.</param>
    /// <param name="storage">Storage de arquivos (local ou S3).</param>
    /// <param name="ct">Token de cancelamento.</param>
    /// <returns>Arquivo criado com a imagem editada.</returns>
    /// <exception cref="InvalidOperationException">Motor sem suporte ou imagem ausente.</exception>
    public async Task<FileEntry> EditAsync(
        string imageId, string prompt, string? size, string userId, IFileStorage storage,
        CancellationToken ct = default)
    {
        var images = await GetConfigAsync(ct);
        if (!images.Enabled || string.IsNullOrWhiteSpace(images.BaseUrl))
        {
            throw new InvalidOperationException("Geração de imagens desabilitada.");
        }

        var engine = engineFactory.Resolve(images.Engine);
        if (!engine.SupportsEdit)
        {
            throw new InvalidOperationException($"O motor '{engine.Name}' não suporta edição de imagem.");
        }

        var source = db.Files.FirstOrDefault(f => f.Id == imageId && f.UserId == userId);
        byte[] sourceBytes;
        try
        {
            if (source is null)
            {
                throw new InvalidOperationException("Imagem de origem não encontrada.");
            }
            await using var sourceStream = await storage.OpenReadAsync(source.StoragePath, ct);
            using var ms = new MemoryStream();
            await sourceStream.CopyToAsync(ms, ct);
            sourceBytes = ms.ToArray();
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Imagem de origem não encontrada.");
        }

        var edited = await engine.EditAsync(images, sourceBytes, prompt, size, ct);
        var files = await PersistAsync([edited], userId, storage, ct);
        return files[0];
    }

    /// <summary>Testa conectividade do motor configurado (admin).</summary>
    public async Task<(bool Ok, string Detail)> TestAsync(CancellationToken ct = default)
    {
        var images = await GetConfigAsync(ct);
        var engine = engineFactory.Resolve(images.Engine);
        return await engine.TestAsync(images, ct);
    }

    private async Task<List<FileEntry>> PersistAsync(
        IReadOnlyList<byte[]> images, string userId, IFileStorage storage, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var files = new List<FileEntry>();
        foreach (var bytes in images)
        {
            var id = Guid.NewGuid().ToString();
            var filename = $"generated-{id[..8]}.png";
            var storagePath = await storage.SaveAsync(
                userId, id, filename, new MemoryStream(bytes), ct);

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
}

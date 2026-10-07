using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services.Video;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Orquestra a geração de vídeos delegando ao motor configurado
/// (<see cref="VideoEngineFactory"/>) e persiste os binários como
/// arquivos do usuário — mesma seam do
/// <see cref="ImageGenerationService"/> (SPEC-20261007-chat-agent-parity
/// RF-019). Config em kv <c>video.config</c> (<see cref="VideoConfig"/>).
/// </summary>
public class VideoGenerationService(
    VideoEngineFactory engineFactory, ConfigService config, AppDbContext db)
{
    private static readonly Dictionary<string, string> ContentTypes = new()
    {
        ["mp4"] = "video/mp4",
        ["webm"] = "video/webm",
        ["mov"] = "video/quicktime",
        ["mkv"] = "video/x-matroska",
        ["webp"] = "image/webp",
        ["gif"] = "image/gif",
    };

    /// <summary>Configuração ativa de geração de vídeo.</summary>
    public Task<VideoConfig> GetConfigAsync(CancellationToken ct = default) =>
        config.GetAsync("video.config", VideoConfig.Default, ct);

    /// <summary>Persiste a configuração de geração de vídeo.</summary>
    public Task SetConfigAsync(VideoConfig video, CancellationToken ct = default) =>
        config.SetAsync("video.config", video, ct);

    /// <summary>
    /// Config resolvida projetada para <see cref="ImagesConfig"/>: quando
    /// <see cref="VideoConfig.Provider"/> aponta para uma conexão OpenAI
    /// cadastrada, URL base e chave vêm dela (engine openai).
    /// </summary>
    /// <param name="ct">Token de cancelamento.</param>
    private async Task<ImagesConfig> GetResolvedImagesConfigAsync(CancellationToken ct = default)
    {
        var video = await GetConfigAsync(ct);
        var resolved = video.ToImagesConfig();
        if (string.IsNullOrWhiteSpace(video.Provider))
        {
            return resolved;
        }

        return await config.FindOpenAiConnectionAsync(video.Provider, ct) is { } conn
            ? resolved with
            {
                Engine = "openai", BaseUrl = conn.Url,
                ApiKey = conn.Key ?? string.Empty,
            }
            : resolved with { BaseUrl = string.Empty, ApiKey = string.Empty };
    }

    /// <summary>Gera vídeo(s) com o motor configurado e grava os arquivos em disco.</summary>
    /// <exception cref="InvalidOperationException">Feature desabilitada ou motor inválido.</exception>
    public async Task<List<FileEntry>> GenerateAsync(
        string prompt, int? seconds, string? size, string userId, string uploadDir,
        CancellationToken ct = default)
    {
        var video = await GetResolvedImagesConfigAsync(ct);
        if (!video.Enabled || string.IsNullOrWhiteSpace(video.BaseUrl))
        {
            throw new InvalidOperationException("Geração de vídeos desabilitada.");
        }

        var engine = engineFactory.Resolve(video.Engine);
        var results = await engine.GenerateAsync(
            video, prompt, seconds,
            string.IsNullOrWhiteSpace(size) ? video.Size : size, ct);
        if (results.Count == 0)
        {
            throw new InvalidOperationException("O motor não retornou vídeos.");
        }

        return await PersistAsync(results, userId, uploadDir, ct);
    }

    /// <summary>Testa conectividade do motor configurado (admin).</summary>
    public async Task<(bool Ok, string Detail)> TestAsync(CancellationToken ct = default)
    {
        var video = await GetResolvedImagesConfigAsync(ct);
        var engine = engineFactory.Resolve(video.Engine);
        return await engine.TestAsync(video, ct);
    }

    private async Task<List<FileEntry>> PersistAsync(
        IReadOnlyList<VideoResult> videos, string userId, string uploadDir, CancellationToken ct)
    {
        Directory.CreateDirectory(uploadDir);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var files = new List<FileEntry>();
        foreach (var video in videos)
        {
            var ext = video.Extension.TrimStart('.').ToLowerInvariant();
            var id = Guid.NewGuid().ToString();
            var filename = $"generated-{id[..8]}.{ext}";
            var storagePath = Path.Combine(uploadDir, $"{id}_{filename}");
            await File.WriteAllBytesAsync(storagePath, video.Bytes, ct);

            var entry = new FileEntry
            {
                Id = id,
                UserId = userId,
                Filename = filename,
                ContentType = ContentTypes.TryGetValue(ext, out var ct2) ? ct2 : "video/mp4",
                StoragePath = storagePath,
                Size = video.Bytes.Length,
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

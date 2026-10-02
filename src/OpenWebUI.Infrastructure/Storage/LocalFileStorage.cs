using OpenWebUI.Application.Interfaces;

namespace OpenWebUI.Infrastructure.Storage;

/// <summary>
/// Storage em disco local (default). Mantém exatamente o layout anterior:
/// <c>{root}/{userId}/{fileId}_{filename}</c> e a chave é o caminho
/// absoluto — registros antigos continuam servíveis sem migração.
/// </summary>
public sealed class LocalFileStorage(string root) : IFileStorage
{
    /// <inheritdoc />
    public string Provider => "local";

    /// <inheritdoc />
    public async Task<string> SaveAsync(
        string userId, string fileId, string filename, Stream content,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(root, userId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{fileId}_{Path.GetFileName(filename)}");
        await using (var fs = File.Create(path))
        {
            await content.CopyToAsync(fs, ct);
        }
        return path;
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default) =>
        Task.FromResult<Stream>(File.OpenRead(storagePath));

    /// <inheritdoc />
    public Task DeleteAsync(string storagePath, CancellationToken ct = default)
    {
        if (File.Exists(storagePath))
        {
            File.Delete(storagePath);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> PingAsync(CancellationToken ct = default) =>
        Task.FromResult(Directory.Exists(root) || TryCreate(root));

    private static bool TryCreate(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

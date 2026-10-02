using OpenWebUI.Application.Interfaces;

namespace OpenWebUI.Infrastructure.Storage;

/// <summary>
/// Storage S3 (AWS ou compatível MinIO). Chaves no formato
/// <c>{userId}/{fileId}_{filename}</c> dentro do bucket; a referência
/// persistida é <c>s3://{bucket}/{key}</c>. Credenciais nunca vão para
/// logs nem respostas.
/// </summary>
public sealed class S3FileStorage(IS3Client s3, string bucket) : IFileStorage
{
    /// <inheritdoc />
    public string Provider => "s3";

    /// <inheritdoc />
    public async Task<string> SaveAsync(
        string userId, string fileId, string filename, Stream content,
        CancellationToken ct = default)
    {
        var key = $"{userId}/{fileId}_{Path.GetFileName(filename)}";
        await s3.PutAsync(bucket, key, content, null, ct);
        return $"s3://{bucket}/{key}";
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default) =>
        await s3.GetAsync(BucketOf(storagePath), KeyOf(storagePath), ct);

    /// <inheritdoc />
    public async Task DeleteAsync(string storagePath, CancellationToken ct = default) =>
        await s3.DeleteAsync(BucketOf(storagePath), KeyOf(storagePath), ct);

    /// <inheritdoc />
    public async Task<bool> PingAsync(CancellationToken ct = default) =>
        await s3.PingAsync(bucket, ct);

    internal static string BucketOf(string storagePath)
    {
        var uri = new Uri(storagePath);
        return uri.Host;
    }

    internal static string KeyOf(string storagePath)
    {
        var uri = new Uri(storagePath);
        return uri.AbsolutePath.TrimStart('/');
    }
}

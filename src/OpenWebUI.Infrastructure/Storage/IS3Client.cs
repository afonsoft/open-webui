using Amazon.S3;
using Amazon.S3.Model;

namespace OpenWebUI.Infrastructure.Storage;

/// <summary>
/// Superfície mínima do cliente S3 usada pelo <see cref="S3FileStorage"/> —
/// permite testar a lógica de chaves/erros sem AWS real.
/// </summary>
public interface IS3Client
{
    /// <summary>PUT do objeto.</summary>
    Task PutAsync(string bucket, string key, Stream content, string? contentType,
        CancellationToken ct);

    /// <summary>GET do objeto (stream deve ser disposed pelo chamador).</summary>
    Task<Stream> GetAsync(string bucket, string key, CancellationToken ct);

    /// <summary>DELETE do objeto.</summary>
    Task DeleteAsync(string bucket, string key, CancellationToken ct);

    /// <summary>Ping do bucket.</summary>
    Task<bool> PingAsync(string bucket, CancellationToken ct);
}

/// <summary>Adapter do <see cref="IAmazonS3"/> oficial para <see cref="IS3Client"/>.</summary>
public sealed class AmazonS3ClientAdapter(IAmazonS3 s3) : IS3Client
{
    /// <inheritdoc />
    public async Task PutAsync(string bucket, string key, Stream content,
        string? contentType, CancellationToken ct) =>
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType ?? "application/octet-stream",
        }, ct);

    /// <inheritdoc />
    public async Task<Stream> GetAsync(string bucket, string key, CancellationToken ct) =>
        (await s3.GetObjectAsync(bucket, key, ct)).ResponseStream;

    /// <inheritdoc />
    public async Task DeleteAsync(string bucket, string key, CancellationToken ct) =>
        await s3.DeleteObjectAsync(bucket, key, ct);

    /// <inheritdoc />
    public async Task<bool> PingAsync(string bucket, CancellationToken ct)
    {
        try
        {
            await s3.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1 }, ct);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

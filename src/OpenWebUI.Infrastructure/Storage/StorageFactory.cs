using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using OpenWebUI.Application.Interfaces;

namespace OpenWebUI.Infrastructure.Storage;

/// <summary>
/// Resolve o <see cref="IFileStorage"/> pela configuração:
/// <c>STORAGE_PROVIDER=local|s3</c> (default <c>local</c>).
/// S3: <c>STORAGE_S3_BUCKET</c> (obrigatório), <c>STORAGE_S3_ENDPOINT</c>
/// (MinIO/compatível), <c>STORAGE_S3_REGION</c>, <c>STORAGE_S3_ACCESS_KEY</c>,
/// <c>STORAGE_S3_SECRET_KEY</c> — credenciais nunca logadas nem expostas.
/// </summary>
public static class StorageFactory
{
    /// <summary>Cria o storage configurado. <paramref name="localRoot"/> é o
    /// diretório de uploads legado (default <c>data/uploads</c>).</summary>
    public static IFileStorage Create(IConfiguration config, string localRoot)
    {
        var provider = (config["STORAGE_PROVIDER"] ?? "local").Trim().ToLowerInvariant();
        if (provider != "s3")
        {
            return new LocalFileStorage(localRoot);
        }

        var bucket = config["STORAGE_S3_BUCKET"];
        if (string.IsNullOrWhiteSpace(bucket))
        {
            throw new InvalidOperationException(
                "STORAGE_PROVIDER=s3 exige STORAGE_S3_BUCKET.");
        }

        var s3Config = new AmazonS3Config
        {
            AuthenticationRegion = config["STORAGE_S3_REGION"] ?? "us-east-1",
        };
        var endpoint = config["STORAGE_S3_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            s3Config.ServiceURL = endpoint;
            s3Config.ForcePathStyle = true; // MinIO e compatíveis
        }

        var accessKey = config["STORAGE_S3_ACCESS_KEY"];
        var secretKey = config["STORAGE_S3_SECRET_KEY"];
        IAmazonS3 client = !string.IsNullOrWhiteSpace(accessKey)
            ? new AmazonS3Client(
                new BasicAWSCredentials(accessKey, secretKey ?? ""), s3Config)
            : new AmazonS3Client(s3Config); // cadeia default (IAM/instance profile)

        return new S3FileStorage(new AmazonS3ClientAdapter(client), bucket);
    }
}

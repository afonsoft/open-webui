using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;

namespace OpenWebUI.Infrastructure.Services.Image;

/// <summary>
/// Base dos motores de imagem: herda os helpers HTTP de
/// <see cref="MediaEngineBase"/> e acrescenta o contrato
/// <see cref="IImageEngine"/> (geração, edição, teste).
/// </summary>
public abstract class ImageEngineBase(IHttpClientFactory httpClientFactory)
    : MediaEngineBase(httpClientFactory), IImageEngine
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public virtual bool SupportsEdit => false;

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<byte[]>> GenerateAsync(
        ImagesConfig config, string prompt, int n, string? size, CancellationToken ct);

    /// <inheritdoc />
    public virtual Task<byte[]> EditAsync(
        ImagesConfig config, byte[] sourceImage, string prompt, string? size, CancellationToken ct) =>
        throw new InvalidOperationException($"O motor '{Name}' não suporta edição de imagem.");

    /// <inheritdoc />
    public abstract Task<(bool Ok, string Detail)> TestAsync(ImagesConfig config, CancellationToken ct);
}

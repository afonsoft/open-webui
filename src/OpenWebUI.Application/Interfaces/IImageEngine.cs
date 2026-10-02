using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Application.Interfaces;

/// <summary>
/// Motor de geração de imagens. Cada implementação conversa com um backend
/// (OpenAI Images, AUTOMATIC1111, Gemini/Imagen, ComfyUI) e devolve os bytes
/// das imagens geradas; a persistência em <c>files</c> fica a cargo do
/// <c>ImageGenerationService</c>.
/// </summary>
public interface IImageEngine
{
    /// <summary>Nome do motor (<c>openai</c>, <c>a1111</c>, <c>gemini</c>, <c>comfyui</c>).</summary>
    string Name { get; }

    /// <summary>Se o motor suporta edição de imagem (img2img/edits).</summary>
    bool SupportsEdit { get; }

    /// <summary>Gera <paramref name="n"/> imagens a partir do prompt.</summary>
    Task<IReadOnlyList<byte[]>> GenerateAsync(
        ImagesConfig config, string prompt, int n, string? size, CancellationToken ct);

    /// <summary>Edita a imagem de origem com o prompt (engines com <see cref="SupportsEdit"/>).</summary>
    Task<byte[]> EditAsync(
        ImagesConfig config, byte[] sourceImage, string prompt, string? size, CancellationToken ct);

    /// <summary>Testa conectividade com o backend configurado.</summary>
    Task<(bool Ok, string Detail)> TestAsync(ImagesConfig config, CancellationToken ct);
}

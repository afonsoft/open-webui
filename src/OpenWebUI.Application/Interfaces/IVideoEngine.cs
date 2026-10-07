using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Application.Interfaces;

/// <summary>
/// Motor de geração de vídeos (SPEC-20261007-chat-agent-parity RF-019):
/// mesma seam dos motores de imagem — cada implementação conversa com um
/// backend (OpenAI Videos, ComfyUI) e devolve os bytes do clipe; a
/// persistência em <c>files</c> fica a cargo do
/// <c>VideoGenerationService</c>. A config transportada é
/// <see cref="ImagesConfig"/>, projetada de <c>VideoConfig</c>.
/// </summary>
public interface IVideoEngine
{
    /// <summary>Nome do motor (<c>openai</c>, <c>comfyui</c>).</summary>
    string Name { get; }

    /// <summary>Gera vídeo(s) a partir do prompt.</summary>
    /// <param name="config">Configuração projetada (base URL, key, timeout, params).</param>
    /// <param name="prompt">Descrição do vídeo.</param>
    /// <param name="seconds">Duração desejada em segundos (null = padrão do backend).</param>
    /// <param name="size">Resolução opcional "WxH".</param>
    /// <param name="ct">Token de cancelamento.</param>
    Task<IReadOnlyList<VideoResult>> GenerateAsync(
        ImagesConfig config, string prompt, int? seconds, string? size, CancellationToken ct);

    /// <summary>Testa conectividade com o backend configurado.</summary>
    Task<(bool Ok, string Detail)> TestAsync(ImagesConfig config, CancellationToken ct);
}

/// <summary>Resultado bruto de uma geração de vídeo (bytes + extensão).</summary>
/// <param name="Bytes">Conteúdo do arquivo.</param>
/// <param name="Extension">Extensão sem ponto: mp4, webm, webp, mov ou gif.</param>
public sealed record VideoResult(byte[] Bytes, string Extension);

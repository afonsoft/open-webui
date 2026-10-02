using OpenWebUI.Application.Interfaces;

namespace OpenWebUI.Infrastructure.Services.Image;

/// <summary>Resolve o motor de imagem configurado por nome.</summary>
public sealed class ImageEngineFactory(IHttpClientFactory httpClientFactory)
{
    private static readonly string[] KnownEngines = ["openai", "a1111", "gemini", "comfyui"];

    /// <summary>Nomes dos motores disponíveis.</summary>
    public static IReadOnlyList<string> Engines => KnownEngines;

    /// <summary>Retorna o motor; nome desconhecido → <see cref="InvalidOperationException"/>.</summary>
    public IImageEngine Resolve(string? engine) => engine?.ToLowerInvariant() switch
    {
        null or "" or "openai" => new OpenAiImageEngine(httpClientFactory),
        "a1111" or "automatic1111" => new A1111Engine(httpClientFactory),
        "gemini" => new GeminiImageEngine(httpClientFactory),
        "comfyui" => new ComfyUiEngine(httpClientFactory),
        var other => throw new InvalidOperationException($"Motor de imagens '{other}' não suportado."),
    };
}

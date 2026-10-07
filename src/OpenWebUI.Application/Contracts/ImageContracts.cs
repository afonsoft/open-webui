namespace OpenWebUI.Application.Contracts;

/// <summary>Configuração de geração de imagens, administrada em Settings → Admin.</summary>
/// <param name="Enabled">Se a geração de imagens está habilitada.</param>
/// <param name="Engine">Motor de geração ("openai").</param>
/// <param name="BaseUrl">URL base do provedor de imagens.</param>
/// <param name="ApiKey">Chave de API do provedor (mascarada nas leituras).</param>
/// <param name="Model">Modelo de imagem (ex.: gpt-image-1, dall-e-3).</param>
/// <param name="Size">Tamanho padrão das imagens geradas.</param>
/// <param name="TimeoutSeconds">Tempo limite da chamada ao provedor, em segundos.</param>
/// <param name="EngineParams">Parâmetros livres do motor em JSON (workflow ComfyUI, steps, cfg, negative_prompt...).</param>
public sealed record ImagesConfig(
    bool Enabled,
    string Engine,
    string BaseUrl,
    string ApiKey,
    string Model,
    string Size,
    int TimeoutSeconds,
    string EngineParams = "{}")
{
    /// <summary>Placeholder exibido no lugar da chave real nas leituras.</summary>
    public const string MaskedApiKey = "********";

    /// <summary>Configuração padrão: desabilitada, motor OpenAI.</summary>
    public static ImagesConfig Default { get; } = new(
        Enabled: false,
        Engine: "openai",
        BaseUrl: "https://api.openai.com/v1",
        ApiKey: string.Empty,
        Model: "gpt-image-1",
        Size: "1024x1024",
        TimeoutSeconds: 120,
        EngineParams: "{}");
}

/// <summary>Pedido de geração de imagem.</summary>
/// <param name="Prompt">Descrição da imagem.</param>
/// <param name="N">Quantidade de imagens (1–4).</param>
/// <param name="Size">Tamanho opcional, sobrescrevendo o padrão configurado.</param>
public sealed record ImageGenerationRequest(string Prompt, int? N, string? Size);

/// <summary>Pedido de edição de imagem (img2img/edits).</summary>
/// <param name="ImageId">Id do arquivo de origem pertencente ao usuário.</param>
/// <param name="Prompt">Instrução de edição.</param>
/// <param name="Size">Tamanho opcional.</param>
public sealed record ImageEditRequest(string ImageId, string Prompt, string? Size);

/// <summary>Resultado do teste de conectividade do motor.</summary>
public sealed record ImageTestResponse(bool Ok, string Detail);

/// <summary>Imagem gerada persistida como arquivo do usuário.</summary>
/// <param name="Url">URL servida pelo endpoint de conteúdo de arquivos.</param>
public sealed record GeneratedImage(string Url);

/// <summary>
/// Configuração de geração de vídeo (SPEC-20261007-chat-agent-parity
/// RF-019), persistida em kv <c>video.config</c>. Projetada para
/// <see cref="ImagesConfig"/> ao invocar os motores — mesmos campos
/// transportados (base URL, key, modelo, tamanho, timeout, params).
/// </summary>
public sealed record VideoConfig(
    bool Enabled,
    string Engine,
    string BaseUrl,
    string ApiKey,
    string Model,
    string Size,
    int TimeoutSeconds,
    string EngineParams = "{}")
{
    /// <summary>Configuração padrão: desabilitada, motor OpenAI (Sora-compatible).</summary>
    public static VideoConfig Default { get; } = new(
        Enabled: false,
        Engine: "openai",
        BaseUrl: "https://api.openai.com/v1",
        ApiKey: string.Empty,
        Model: "sora-2",
        Size: "1280x720",
        TimeoutSeconds: 600,
        EngineParams: "{}");

    /// <summary>Projeta para a config usada pelos motores de mídia.</summary>
    public ImagesConfig ToImagesConfig() => new(
        Enabled, Engine, BaseUrl, ApiKey, Model, Size, TimeoutSeconds, EngineParams);
}

/// <summary>Pedido de geração de vídeo.</summary>
/// <param name="Seconds">Duração desejada em segundos (opcional, 1–60).</param>
public sealed record VideoGenerationRequest(string Prompt, int? Seconds, string? Size);

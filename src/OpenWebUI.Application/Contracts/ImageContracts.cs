namespace OpenWebUI.Application.Contracts;

/// <summary>Configuração de geração de imagens, administrada em Settings → Admin.</summary>
/// <param name="Enabled">Se a geração de imagens está habilitada.</param>
/// <param name="Engine">Motor de geração ("openai").</param>
/// <param name="BaseUrl">URL base do provedor de imagens.</param>
/// <param name="ApiKey">Chave de API do provedor (mascarada nas leituras).</param>
/// <param name="Model">Modelo de imagem (ex.: gpt-image-1, dall-e-3).</param>
/// <param name="Size">Tamanho padrão das imagens geradas.</param>
/// <param name="TimeoutSeconds">Tempo limite da chamada ao provedor, em segundos.</param>
public sealed record ImagesConfig(
    bool Enabled,
    string Engine,
    string BaseUrl,
    string ApiKey,
    string Model,
    string Size,
    int TimeoutSeconds)
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
        TimeoutSeconds: 120);
}

/// <summary>Pedido de geração de imagem.</summary>
/// <param name="Prompt">Descrição da imagem.</param>
/// <param name="N">Quantidade de imagens (1–4).</param>
/// <param name="Size">Tamanho opcional, sobrescrevendo o padrão configurado.</param>
public sealed record ImageGenerationRequest(string Prompt, int? N, string? Size);

/// <summary>Imagem gerada persistida como arquivo do usuário.</summary>
/// <param name="Url">URL servida pelo endpoint de conteúdo de arquivos.</param>
public sealed record GeneratedImage(string Url);

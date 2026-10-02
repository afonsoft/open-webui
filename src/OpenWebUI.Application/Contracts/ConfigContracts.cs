namespace OpenWebUI.Application.Contracts;

/// <summary>Configuração de conexões com provedores de IA (espelha Admin Settings → Connections).</summary>
/// <param name="OllamaBaseUrls">URLs base de servidores Ollama.</param>
/// <param name="OpenAiBaseUrls">URLs base de APIs compatíveis com OpenAI.</param>
/// <param name="OpenAiApiKeys">Chaves de API correspondentes às URLs OpenAI.</param>
public sealed record ConnectionsConfig(
    IReadOnlyList<string> OllamaBaseUrls,
    IReadOnlyList<string> OpenAiBaseUrls,
    IReadOnlyList<string> OpenAiApiKeys)
{
    /// <summary>Configuração padrão apontando para um Ollama local.</summary>
    public static ConnectionsConfig Default { get; } = new(
        OllamaBaseUrls: ["http://localhost:11434"],
        OpenAiBaseUrls: [],
        OpenAiApiKeys: []);
}

/// <summary>Visão pública das conexões: chaves de API são omitidas.</summary>
/// <param name="OllamaBaseUrls">URLs base de servidores Ollama.</param>
/// <param name="OpenAiBaseUrls">URLs base de APIs compatíveis com OpenAI.</param>
/// <param name="OpenAiKeyConfigured">Indica se existe chave configurada por URL.</param>
public sealed record ConnectionsConfigResponse(
    IReadOnlyList<string> OllamaBaseUrls,
    IReadOnlyList<string> OpenAiBaseUrls,
    IReadOnlyList<bool> OpenAiKeyConfigured);

/// <summary>Resposta de versão da API.</summary>
/// <param name="Version">Versão do backend .NET.</param>
public sealed record VersionResponse(string Version);

/// <summary>Resposta de verificação de atualizações.</summary>
/// <param name="Current">Versão atual.</param>
/// <param name="Latest">Última versão conhecida.</param>
public sealed record VersionUpdateResponse(string Current, string Latest);

/// <summary>Configuração pública da aplicação consumida pelo frontend antes do login.</summary>
/// <param name="Status">Indica que o backend está no ar.</param>
/// <param name="Name">Nome da instância.</param>
/// <param name="Version">Versão do backend.</param>
/// <param name="DefaultLocale">Localidade padrão.</param>
/// <param name="Features">Flags de funcionalidades habilitadas.</param>
/// <param name="DefaultPromptSuggestions">Sugestões de prompt para chat vazio.</param>
public sealed record AppConfigResponse(
    bool Status,
    string Name,
    string Version,
    string DefaultLocale,
    AppFeatures Features,
    IReadOnlyList<string> DefaultModels,
    IReadOnlyList<PromptSuggestion> DefaultPromptSuggestions,
    IReadOnlyList<string> OAuthProviders);

/// <summary>Flags de funcionalidades expostas pelo /api/config.</summary>
/// <param name="Auth">Se autenticação está ativa.</param>
/// <param name="EnableSignup">Cadastro aberto.</param>
/// <param name="EnableLoginForm">Formulário de login habilitado.</param>
/// <param name="EnableApiKeys">Chaves de API habilitadas.</param>
/// <param name="EnableMessageRating">Avaliação de respostas habilitada.</param>
/// <param name="EnableFolders">Pastas habilitadas.</param>
/// <param name="EnableMemories">Memórias habilitadas.</param>
/// <param name="EnableNotes">Notas habilitadas.</param>
/// <param name="EnableChannels">Canais habilitados (não implementado).</param>
/// <param name="EnableWebSearch">Busca web habilitada (não implementado).</param>
/// <param name="EnableImageGeneration">Geração de imagens habilitada (não implementado).</param>
/// <param name="EnableCodeExecution">Execução de código habilitada (não implementado).</param>
/// <param name="EnableCommunitySharing">Compartilhamento comunitário habilitado.</param>
public sealed record AppFeatures(
    bool Auth,
    bool EnableSignup,
    bool EnableLoginForm,
    bool EnableApiKeys,
    bool EnableMessageRating,
    bool EnableFolders,
    bool EnableMemories,
    bool EnableNotes,
    bool EnableChannels,
    bool EnableWebSearch,
    bool EnableImageGeneration,
    bool EnableCodeExecution,
    bool EnableCommunitySharing);

/// <summary>Sugestão de prompt exibida no estado vazio do chat.</summary>
/// <param name="Title">Rótulo da sugestão.</param>
/// <param name="Content">Texto inserido no input ao clicar.</param>
public sealed class PromptSuggestion
{
    /// <summary>Cria uma sugestão vazia (para binding).</summary>
    public PromptSuggestion() { }

    /// <summary>Cria uma sugestão com título e conteúdo.</summary>
    public PromptSuggestion(string title, string content)
    {
        Title = title;
        Content = content;
    }

    /// <summary>Rótulo da sugestão.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Texto inserido no input ao clicar.</summary>
    public string Content { get; set; } = string.Empty;
}

/// <summary>Configuração de modelos padrão e sugestões de prompt.</summary>
/// <param name="DefaultModels">Ids de modelos selecionados por padrão (ordenados).</param>
/// <param name="PromptSuggestions">Sugestões exibidas no chat vazio.</param>
public sealed record ModelsConfig(
    List<string> DefaultModels,
    List<PromptSuggestion> PromptSuggestions)
{
    /// <summary>Configuração vazia (sem defaults).</summary>
    public static readonly ModelsConfig Empty = new([], []);
}

/// <summary>Resposta de um banner.</summary>
public sealed record BannerResponse(
    string Id, string Type, string Title, string Content, bool Dismissible, long Timestamp);

/// <summary>Criação/atualização de banner.</summary>
/// <param name="Type">info | warning | error | success.</param>
/// <param name="Title">Título.</param>
/// <param name="Content">Conteúdo.</param>
/// <param name="Dismissible">Se pode ser dispensado (default true).</param>
public sealed record BannerRequest(string Type, string Title, string Content, bool? Dismissible = null);

/// <summary>Toggle booleano genérico de feature.</summary>
/// <param name="Enabled">Se a feature está habilitada.</param>
public sealed record FeatureToggle(bool Enabled);

/// <summary>Configuração de cadastro.</summary>
/// <param name="EnableSignup">Cadastro aberto.</param>
/// <param name="DefaultUserRole">Papel de novos usuários (pending/user/admin).</param>
public sealed record SignupConfig(bool EnableSignup, string DefaultUserRole);

/// <summary>Configuração de execução de código.</summary>
/// <param name="Engines">Engines habilitados (pyodide, jupyter).</param>
/// <param name="DirectConnections">Se conexões diretas do cliente estão habilitadas.</param>
public sealed record CodeExecutionConfig(List<string> Engines, bool DirectConnections);

/// <summary>Configuração de expiração do JWT.</summary>
/// <param name="ExpiresIn">Duração textual (ex.: "7d", "24h", "30m") ou segundos.</param>
public sealed record JwtExpiryConfig(string ExpiresIn);

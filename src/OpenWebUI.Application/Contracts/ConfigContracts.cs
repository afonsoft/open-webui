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
    IReadOnlyList<string> DefaultPromptSuggestions);

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

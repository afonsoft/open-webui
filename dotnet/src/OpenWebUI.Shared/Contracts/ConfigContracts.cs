namespace OpenWebUI.Shared.Contracts;

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

namespace OpenWebUI.Shared.Contracts;

/// <summary>Descrição de um modelo disponível para chat.</summary>
/// <param name="Id">Identificador usado nas requisições de completion.</param>
/// <param name="Name">Nome amigável exibido na UI.</param>
/// <param name="Provider">Provedor de origem: "ollama" ou "openai".</param>
/// <param name="OwnedBy">Dono reportado pelo provedor, quando disponível.</param>
public sealed record ModelInfo(string Id, string Name, string Provider, string? OwnedBy);

/// <summary>Lista agregada de modelos, no formato OpenAI (<c>{ data: [...] }</c>).</summary>
/// <param name="Data">Modelos de todos os provedores configurados.</param>
public sealed record ModelListResponse(IReadOnlyList<ModelInfo> Data);

/// <summary>Mensagem enviada ao endpoint de chat completion.</summary>
/// <param name="Role">Papel: system, user ou assistant.</param>
/// <param name="Content">Conteúdo textual.</param>
public sealed record ChatCompletionMessage(string Role, string Content);

/// <summary>Requisição de chat completion compatível com OpenAI.</summary>
/// <param name="Model">Identificador do modelo.</param>
/// <param name="Messages">Histórico de mensagens.</param>
/// <param name="Stream">Se a resposta deve ser transmitida via SSE.</param>
/// <param name="Connection">Provedor preferencial ("ollama"/"openai"), opcional.</param>
public sealed record ChatCompletionRequest(
    string Model,
    IReadOnlyList<ChatCompletionMessage> Messages,
    bool Stream = true,
    string? Connection = null);

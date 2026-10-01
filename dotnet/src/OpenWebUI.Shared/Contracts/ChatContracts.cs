namespace OpenWebUI.Shared.Contracts;

/// <summary>Uma mensagem dentro de um chat (formato OpenAI simplificado).</summary>
/// <param name="Id">Identificador estável da mensagem.</param>
/// <param name="Role">Papel: system, user ou assistant.</param>
/// <param name="Content">Conteúdo textual (Markdown).</param>
/// <param name="Model">Modelo que gerou a mensagem (somente assistant).</param>
/// <param name="Timestamp">Instante de criação em UTC (epoch seconds).</param>
public sealed record ChatMessageModel(
    string Id,
    string Role,
    string Content,
    string? Model,
    long Timestamp);

/// <summary>Resumo de um chat para listagem na barra lateral.</summary>
/// <param name="Id">Identificador do chat.</param>
/// <param name="Title">Título exibido.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="UpdatedAt">Última atualização (epoch seconds).</param>
public sealed record ChatSummaryResponse(string Id, string Title, long CreatedAt, long UpdatedAt);

/// <summary>Chat completo com histórico linear de mensagens.</summary>
/// <param name="Id">Identificador do chat.</param>
/// <param name="Title">Título exibido.</param>
/// <param name="Models">Modelos associados ao chat.</param>
/// <param name="Messages">Mensagens em ordem cronológica.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="UpdatedAt">Última atualização (epoch seconds).</param>
public sealed record ChatResponse(
    string Id,
    string Title,
    IReadOnlyList<string> Models,
    IReadOnlyList<ChatMessageModel> Messages,
    long CreatedAt,
    long UpdatedAt);

/// <summary>Requisição para criar ou atualizar um chat.</summary>
/// <param name="Title">Título do chat.</param>
/// <param name="Models">Modelos selecionados.</param>
/// <param name="Messages">Histórico completo de mensagens.</param>
public sealed record ChatUpsertRequest(
    string Title,
    IReadOnlyList<string> Models,
    IReadOnlyList<ChatMessageModel> Messages);

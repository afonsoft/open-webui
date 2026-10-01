namespace OpenWebUI.Application.Contracts;

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
/// <param name="Pinned">Se está fixado no topo.</param>
/// <param name="FolderId">Pasta que contém o chat, se houver.</param>
/// <param name="Tags">Tags associadas.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="UpdatedAt">Última atualização (epoch seconds).</param>
public sealed record ChatSummaryResponse(
    string Id,
    string Title,
    bool Pinned,
    string? FolderId,
    IReadOnlyList<string> Tags,
    long CreatedAt,
    long UpdatedAt);

/// <summary>Chat completo com histórico linear de mensagens.</summary>
/// <param name="Id">Identificador do chat.</param>
/// <param name="Title">Título exibido.</param>
/// <param name="Models">Modelos associados ao chat.</param>
/// <param name="Messages">Mensagens em ordem cronológica.</param>
/// <param name="Pinned">Se está fixado.</param>
/// <param name="Archived">Se está arquivado.</param>
/// <param name="Tags">Tags associadas.</param>
/// <param name="FolderId">Pasta que contém o chat.</param>
/// <param name="ShareId">Id público de compartilhamento, quando ativo.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="UpdatedAt">Última atualização (epoch seconds).</param>
public sealed record ChatResponse(
    string Id,
    string Title,
    IReadOnlyList<string> Models,
    IReadOnlyList<ChatMessageModel> Messages,
    bool Pinned,
    bool Archived,
    IReadOnlyList<string> Tags,
    string? FolderId,
    string? ShareId,
    IReadOnlyList<string> ToolIds,
    long CreatedAt,
    long UpdatedAt);

/// <summary>Requisição para criar ou atualizar um chat.</summary>
/// <param name="Title">Título do chat.</param>
/// <param name="Models">Modelos selecionados.</param>
/// <param name="Messages">Histórico completo de mensagens.</param>
/// <param name="ToolIds">Ids das tools habilitadas neste chat (opcional).</param>
public sealed record ChatUpsertRequest(
    string Title,
    IReadOnlyList<string> Models,
    IReadOnlyList<ChatMessageModel> Messages,
    IReadOnlyList<string>? ToolIds = null);

/// <summary>Atualização parcial de metadados do chat (título/tags/pasta).</summary>
/// <param name="Title">Novo título (opcional).</param>
/// <param name="Tags">Novas tags (opcional).</param>
/// <param name="FolderId">Nova pasta (opcional; string vazia remove da pasta).</param>
public sealed record ChatMetaUpdateRequest(
    string? Title,
    IReadOnlyList<string>? Tags,
    string? FolderId);

/// <summary>Atualização do conteúdo de uma mensagem existente.</summary>
/// <param name="Content">Novo conteúdo da mensagem.</param>
public sealed record MessageUpdateRequest(string Content);

/// <summary>Corpo de endpoints que retornam somente sucesso/estado.</summary>
/// <param name="Success">Indica se a operação foi concluída.</param>
public sealed record StatusResponse(bool Success);

namespace OpenWebUI.Shared.Contracts;

/// <summary>Pasta de organização de chats.</summary>
/// <param name="Id">Identificador da pasta.</param>
/// <param name="Name">Nome exibido.</param>
/// <param name="ParentId">Pasta-pai, quando aninhada.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="UpdatedAt">Última atualização (epoch seconds).</param>
public sealed record FolderResponse(string Id, string Name, string? ParentId, long CreatedAt, long UpdatedAt);

/// <summary>Criação/atualização de pasta.</summary>
/// <param name="Name">Nome da pasta.</param>
/// <param name="ParentId">Pasta-pai opcional.</param>
public sealed record FolderUpsertRequest(string Name, string? ParentId);

/// <summary>Prompt personalizado acionado por /comando.</summary>
/// <param name="Id">Identificador do prompt.</param>
/// <param name="Command">Comando (sem a barra).</param>
/// <param name="Title">Título exibido.</param>
/// <param name="Content">Texto do prompt.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="UpdatedAt">Última atualização (epoch seconds).</param>
public sealed record PromptResponse(
    string Id, string Command, string Title, string Content, long CreatedAt, long UpdatedAt);

/// <summary>Criação/atualização de prompt.</summary>
/// <param name="Command">Comando (sem a barra).</param>
/// <param name="Title">Título.</param>
/// <param name="Content">Texto do prompt.</param>
public sealed record PromptUpsertRequest(string Command, string Title, string Content);

/// <summary>Metadados de um arquivo enviado.</summary>
/// <param name="Id">Identificador do arquivo.</param>
/// <param name="Filename">Nome original.</param>
/// <param name="ContentType">Tipo MIME.</param>
/// <param name="Size">Tamanho em bytes.</param>
/// <param name="HasText">Indica se há texto extraído disponível.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
public sealed record FileResponse(
    string Id, string Filename, string? ContentType, long Size, bool HasText, long CreatedAt);

/// <summary>Conteúdo textual extraído de um arquivo.</summary>
/// <param name="Content">Texto extraído (vazio quando o formato não é texto).</param>
public sealed record FileContentResponse(string Content);

/// <summary>Modelo personalizado do workspace.</summary>
/// <param name="Id">Identificador interno.</param>
/// <param name="Name">Nome público.</param>
/// <param name="BaseModelId">Modelo base do provedor.</param>
/// <param name="SystemPrompt">System prompt aplicado.</param>
/// <param name="ParamsJson">Parâmetros de geração em JSON.</param>
/// <param name="ProfileImageUrl">Imagem do modelo.</param>
/// <param name="IsActive">Se está visível no seletor.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="UpdatedAt">Última atualização (epoch seconds).</param>
public sealed record ModelEntryResponse(
    string Id,
    string Name,
    string BaseModelId,
    string? SystemPrompt,
    string? ParamsJson,
    string? ProfileImageUrl,
    bool IsActive,
    long CreatedAt,
    long UpdatedAt);

/// <summary>Criação/atualização de modelo personalizado.</summary>
/// <param name="Name">Nome público.</param>
/// <param name="BaseModelId">Modelo base do provedor.</param>
/// <param name="SystemPrompt">System prompt.</param>
/// <param name="ParamsJson">Parâmetros de geração em JSON.</param>
/// <param name="ProfileImageUrl">Imagem do modelo.</param>
public sealed record ModelEntryUpsertRequest(
    string Name, string BaseModelId, string? SystemPrompt, string? ParamsJson, string? ProfileImageUrl);

/// <summary>Memória persistente do usuário.</summary>
/// <param name="Id">Identificador.</param>
/// <param name="Content">Conteúdo lembrado.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="UpdatedAt">Última atualização (epoch seconds).</param>
public sealed record MemoryResponse(string Id, string Content, long CreatedAt, long UpdatedAt);

/// <summary>Criação/atualização de memória.</summary>
/// <param name="Content">Conteúdo a lembrar.</param>
public sealed record MemoryUpsertRequest(string Content);

/// <summary>Nota do usuário.</summary>
/// <param name="Id">Identificador.</param>
/// <param name="Title">Título.</param>
/// <param name="Content">Conteúdo em Markdown.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="UpdatedAt">Última atualização (epoch seconds).</param>
public sealed record NoteResponse(string Id, string Title, string Content, long CreatedAt, long UpdatedAt);

/// <summary>Criação/atualização de nota.</summary>
/// <param name="Title">Título.</param>
/// <param name="Content">Conteúdo em Markdown.</param>
public sealed record NoteUpsertRequest(string Title, string Content);

/// <summary>Avaliação registrada em uma mensagem.</summary>
/// <param name="Id">Identificador da avaliação.</param>
/// <param name="ChatId">Chat da mensagem.</param>
/// <param name="MessageId">Mensagem avaliada.</param>
/// <param name="ModelId">Modelo que produziu a mensagem.</param>
/// <param name="Rating">1 (positivo) ou -1 (negativo).</param>
/// <param name="Reason">Motivo opcional.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
public sealed record FeedbackResponse(
    string Id, string ChatId, string MessageId, string? ModelId, int Rating, string? Reason, long CreatedAt);

/// <summary>Registro de avaliação em mensagem.</summary>
/// <param name="ChatId">Chat da mensagem.</param>
/// <param name="MessageId">Mensagem avaliada.</param>
/// <param name="ModelId">Modelo que produziu a mensagem.</param>
/// <param name="Rating">1 (positivo) ou -1 (negativo).</param>
/// <param name="Reason">Motivo opcional.</param>
public sealed record FeedbackUpsertRequest(
    string ChatId, string MessageId, string? ModelId, int Rating, string? Reason);

/// <summary>Requisição de geração auxiliar de tarefa (título, follow-ups, tags).</summary>
/// <param name="Model">Modelo a usar.</param>
/// <param name="Messages">Mensagens de contexto do chat.</param>
/// <param name="ChatId">Chat relacionado (opcional).</param>
public sealed record TaskGenerationRequest(
    string Model, IReadOnlyList<ChatCompletionMessage> Messages, string? ChatId);

/// <summary>Resposta de geração de título.</summary>
/// <param name="Title">Título sugerido.</param>
public sealed record TaskTitleResponse(string Title);

/// <summary>Resposta de geração de follow-ups.</summary>
/// <param name="FollowUps">Sugestões de próximas mensagens.</param>
public sealed record TaskFollowUpsResponse(IReadOnlyList<string> FollowUps);

/// <summary>Resposta de geração de tags.</summary>
/// <param name="Tags">Tags sugeridas.</param>
public sealed record TaskTagsResponse(IReadOnlyList<string> Tags);

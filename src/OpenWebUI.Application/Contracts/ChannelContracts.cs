namespace OpenWebUI.Application.Contracts;

/// <summary>Resumo de canal na listagem.</summary>
/// <param name="Id">Identificador do canal.</param>
/// <param name="Name">Nome.</param>
/// <param name="Description">Descrição opcional.</param>
/// <param name="Type">Tipo ("channel").</param>
/// <param name="MemberCount">Total de membros.</param>
/// <param name="MyRole">Papel do chamador no canal ("admin"/"member").</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
public sealed record ChannelResponse(
    string Id,
    string Name,
    string? Description,
    string Type,
    int MemberCount,
    string MyRole,
    long CreatedAt);

/// <summary>Canal com a lista de membros.</summary>
/// <param name="Members">Membros com papel.</param>
public sealed record ChannelDetailResponse(
    string Id,
    string Name,
    string? Description,
    string Type,
    string MyRole,
    long CreatedAt,
    IReadOnlyList<ChannelMemberResponse> Members);

/// <summary>Membro de canal.</summary>
/// <param name="UserId">Usuário.</param>
/// <param name="Name">Nome de exibição.</param>
/// <param name="ProfileImageUrl">Avatar.</param>
/// <param name="Role">Papel no canal ("admin"/"member").</param>
public sealed record ChannelMemberResponse(
    string UserId,
    string Name,
    string? ProfileImageUrl,
    string Role);

/// <summary>Mensagem de canal.</summary>
/// <param name="Id">Identificador.</param>
/// <param name="ChannelId">Canal.</param>
/// <param name="UserId">Autor usuário (nulo = modelo).</param>
/// <param name="ModelId">Modelo autor (quando UserId é nulo).</param>
/// <param name="AuthorName">Nome exibido (usuário ou modelo).</param>
/// <param name="ProfileImageUrl">Avatar do autor usuário.</param>
/// <param name="Content">Conteúdo.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
public sealed record ChannelMessageResponse(
    string Id,
    string ChannelId,
    string? UserId,
    string? ModelId,
    string AuthorName,
    string? ProfileImageUrl,
    string Content,
    long CreatedAt);

/// <summary>Criação de canal.</summary>
/// <param name="Name">Nome obrigatório.</param>
/// <param name="Description">Descrição opcional.</param>
/// <param name="MemberIds">Usuários adicionais (criador entra como admin).</param>
public sealed record CreateChannelRequest(
    string Name,
    string? Description,
    string[]? MemberIds);

/// <summary>Atualização de canal.</summary>
/// <param name="Name">Novo nome (opcional).</param>
/// <param name="Description">Nova descrição (opcional).</param>
public sealed record UpdateChannelRequest(
    string? Name,
    string? Description);

/// <summary>Nova mensagem de canal.</summary>
/// <param name="Content">Texto; "@modelo pergunta" invoca o modelo.</param>
public sealed record CreateChannelMessageRequest(string Content);

/// <summary>Inclusão de membros no canal.</summary>
/// <param name="UserIds">Usuários a adicionar.</param>
/// <param name="Role">Papel ("admin"/"member", default "member").</param>
public sealed record AddChannelMembersRequest(
    string[] UserIds,
    string? Role);

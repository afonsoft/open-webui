namespace OpenWebUI.Application.Contracts;

/// <summary>Flags de permissão de workspace de um grupo.</summary>
/// <param name="Models">Criar/gerenciar modelos customizados.</param>
/// <param name="Prompts">Criar/gerenciar prompts.</param>
/// <param name="Knowledge">Gerenciar bases de conhecimento.</param>
/// <param name="Tools">Gerenciar tools/functions.</param>
/// <param name="Files">Upload de arquivos.</param>
public sealed record WorkspacePermissions(
    bool Models = true,
    bool Prompts = true,
    bool Knowledge = true,
    bool Tools = true,
    bool Files = true);

/// <summary>Flags de compartilhamento de um grupo.</summary>
/// <param name="PublicChats">Compartilhar chats publicamente.</param>
public sealed record SharingPermissions(bool PublicChats = true);

/// <summary>Flags de chat de um grupo.</summary>
/// <param name="Controls">Controles de chat (edição, regeneração, avaliação).</param>
public sealed record ChatPermissions(bool Controls = true);

/// <summary>Permissões granulares de um grupo, espelhando o upstream.</summary>
/// <param name="Workspace">Permissões de workspace.</param>
/// <param name="Sharing">Permissões de compartilhamento.</param>
/// <param name="Chat">Permissões de chat.</param>
public sealed record GroupPermissions(
    WorkspacePermissions? Workspace = null,
    SharingPermissions? Sharing = null,
    ChatPermissions? Chat = null)
{
    /// <summary>Permissões completas (default).</summary>
    public static readonly GroupPermissions Full =
        new(new WorkspacePermissions(), new SharingPermissions(), new ChatPermissions());
}

/// <summary>Membro de um grupo em respostas de API.</summary>
/// <param name="UserId">Id do usuário.</param>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Email">E-mail.</param>
/// <param name="Role">Papel no grupo (admin/member).</param>
public sealed record GroupMemberResponse(string UserId, string Name, string Email, string Role);

/// <summary>Grupo em respostas de API.</summary>
/// <param name="Id">Id do grupo.</param>
/// <param name="Name">Nome.</param>
/// <param name="Description">Descrição.</param>
/// <param name="Permissions">Flags de permissão.</param>
/// <param name="Members">Membros.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="UpdatedAt">Atualização (epoch seconds).</param>
public sealed record GroupResponse(
    string Id,
    string Name,
    string? Description,
    GroupPermissions Permissions,
    IReadOnlyList<GroupMemberResponse> Members,
    long CreatedAt,
    long UpdatedAt);

/// <summary>Criação de grupo.</summary>
/// <param name="Name">Nome do grupo.</param>
/// <param name="Description">Descrição opcional.</param>
/// <param name="Permissions">Flags de permissão (default: todas).</param>
public sealed record CreateGroupRequest(
    string Name,
    string? Description,
    GroupPermissions? Permissions);

/// <summary>Atualização parcial de grupo.</summary>
/// <param name="Name">Novo nome (opcional).</param>
/// <param name="Description">Nova descrição (opcional).</param>
/// <param name="Permissions">Novas permissões (opcional).</param>
public sealed record UpdateGroupRequest(
    string? Name,
    string? Description,
    GroupPermissions? Permissions);

/// <summary>Adição de membros a um grupo.</summary>
/// <param name="UserIds">Ids dos usuários a adicionar.</param>
/// <param name="Role">Papel no grupo (admin/member; default member).</param>
public sealed record UpdateGroupMembersRequest(
    IReadOnlyList<string> UserIds,
    string? Role = null);

/// <summary>Resultado do vínculo/criação de usuário via OAuth.</summary>
/// <param name="UserId">Id do usuário local.</param>
/// <param name="NewUser">Se um usuário novo foi criado.</param>
public sealed record OAuthLinkResult(string UserId, bool NewUser);

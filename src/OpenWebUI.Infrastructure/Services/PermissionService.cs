using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Avalia permissões granulares por união dos grupos do usuário.
/// Admin sempre tem tudo; usuário sem grupo mantém o comportamento default
/// (todas as permissões, como antes dos grupos existirem).
/// </summary>
public class PermissionService(AppDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Permissão <c>sharing.public_chats</c> (compartilhar chat publicamente).</summary>
    public const string SharingPublicChats = "sharing.public_chats";

    /// <summary>Permissão <c>workspace.prompts</c>.</summary>
    public const string WorkspacePrompts = "workspace.prompts";

    /// <summary>Permissão <c>workspace.models</c>.</summary>
    public const string WorkspaceModels = "workspace.models";

    /// <summary>Permissão <c>workspace.files</c>.</summary>
    public const string WorkspaceFiles = "workspace.files";

    /// <summary>Permissão <c>workspace.knowledge</c>.</summary>
    public const string WorkspaceKnowledge = "workspace.knowledge";

    /// <summary>Permissão <c>workspace.tools</c>.</summary>
    public const string WorkspaceTools = "workspace.tools";

    /// <summary>Permissão <c>chat.controls</c>.</summary>
    public const string ChatControls = "chat.controls";

    /// <summary>Avalia se o usuário possui a permissão (união dos grupos).</summary>
    /// <param name="user">Usuário avaliado.</param>
    /// <param name="permission">Chave no formato "seção.flag".</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<bool> HasAsync(User user, string permission, CancellationToken ct = default)
    {
        if (user.Role == UserRoles.Admin)
        {
            return true;
        }

        var groups = await db.GroupMembers
            .Where(m => m.UserId == user.Id)
            .Select(m => m.Group!.PermissionsJson)
            .ToListAsync(ct);
        if (groups.Count == 0)
        {
            return true; // default: permissões atuais preservadas
        }

        return groups.Any(json => Applies(json, permission));
    }

    private static bool Applies(string permissionsJson, string permission)
    {
        GroupPermissions? permissions;
        try
        {
            permissions = JsonSerializer.Deserialize<GroupPermissions>(permissionsJson, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (permissions is null)
        {
            return false;
        }

        var workspace = permissions.Workspace ?? new WorkspacePermissions();
        var sharing = permissions.Sharing ?? new SharingPermissions();
        var chat = permissions.Chat ?? new ChatPermissions();

        return permission switch
        {
            SharingPublicChats => sharing.PublicChats,
            WorkspacePrompts => workspace.Prompts,
            WorkspaceModels => workspace.Models,
            WorkspaceFiles => workspace.Files,
            WorkspaceKnowledge => workspace.Knowledge,
            WorkspaceTools => workspace.Tools,
            ChatControls => chat.Controls,
            _ => true,
        };
    }
}

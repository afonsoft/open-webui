using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Hubs;

/// <summary>
/// Hub realtime de canais: entrega message:new, user:typing e presence
/// aos membros conectados. JWT chega via query access_token no handshake.
/// </summary>
[Authorize]
public class ChatHub(AppDbContext db, AccessControlService access) : Hub
{
    /// <summary>Conexões ativas por usuário (multi-aba).</summary>
    private static readonly ConcurrentDictionary<string, int> ConnectionsPerUser = new();

    /// <summary>Usuários online por canal.</summary>
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Presence = new();

    /// <summary>Presença/cursores por nota: noteId → connectionId → entrada de awareness.</summary>
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, NotePresenceEntry>> NotePresenceMap = new();

    /// <summary>Paleta de cores de cursores remotos (índice via hash do userId).</summary>
    private static readonly string[] CollabPalette =
        ["#e0533d", "#2f81f7", "#3fb950", "#d29922", "#a371f7", "#f778ba", "#39c5cf", "#ffa657"];

    /// <summary>Nome do grupo SignalR de um canal.</summary>
    public static string GroupName(string channelId) => $"channel-{channelId}";

    /// <summary>Nome do grupo SignalR de um chat (eventos de edição de mensagens).</summary>
    public static string ChatGroupName(string chatId) => $"chat-{chatId}";

    /// <summary>Entra no grupo de um chat (dono ou admin) para receber eventos de edição.</summary>
    /// <param name="chatId">Chat a acompanhar.</param>
    public async Task JoinChat(string chatId)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var isAdmin = Context.User?.IsInRole(UserRoles.Admin) == true;
        var allowed = userId is not null && (isAdmin ||
            await db.Chats.AnyAsync(c => c.Id == chatId && c.UserId == userId));
        if (allowed)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, ChatGroupName(chatId));
        }
    }

    /// <summary>Sai do grupo de um chat.</summary>
    /// <param name="chatId">Chat a deixar de acompanhar.</param>
    public async Task LeaveChat(string chatId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ChatGroupName(chatId));

    /// <summary>Ids de canais onde o usuário está online.</summary>
    public static IReadOnlyList<string> OnlineUsers(string channelId) =>
        Presence.TryGetValue(channelId, out var set) ? set.Keys.ToArray() : [];

    /// <summary>Ids de usuários com ao menos uma conexão aberta no hub.</summary>
    public static IReadOnlyList<string> ConnectedUserIds() =>
        ConnectionsPerUser.Keys.ToArray();

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            await base.OnConnectedAsync();
            return;
        }

        ConnectionsPerUser.AddOrUpdate(userId, 1, (_, count) => count + 1);

        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastActiveAt,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        var channelIds = await db.ChannelMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.ChannelId)
            .ToListAsync();

        foreach (var channelId in channelIds)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(channelId));
            var online = Presence.GetOrAdd(channelId, _ => new ConcurrentDictionary<string, byte>());
            online[userId] = 1;
            await Clients.Group(GroupName(channelId))
                .SendAsync("presence", channelId, online.Keys.ToArray());
        }

        await base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Remove a conexão dos rosters de notas colaborativas.
        foreach (var (noteId, members) in NotePresenceMap)
        {
            if (members.TryRemove(Context.ConnectionId, out _))
            {
                await BroadcastNotePresence(noteId);
            }
        }

        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is not null)
        {
            var remaining = ConnectionsPerUser.AddOrUpdate(
                userId, 0, (_, count) => Math.Max(0, count - 1));
            if (remaining == 0)
            {
                ConnectionsPerUser.TryRemove(userId, out _);
                var channelIds = await db.ChannelMembers.AsNoTracking()
                    .Where(m => m.UserId == userId)
                    .Select(m => m.ChannelId)
                    .ToListAsync();

                foreach (var channelId in channelIds)
                {
                    if (Presence.TryGetValue(channelId, out var online))
                    {
                        online.TryRemove(userId, out _);
                        await Clients.Group(GroupName(channelId))
                            .SendAsync("presence", channelId, online.Keys.ToArray());
                    }
                }
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Propaga indicador de digitação aos demais membros do canal.</summary>
    /// <param name="channelId">Canal onde o usuário está digitando.</param>
    public async Task Typing(string channelId)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var name = Context.User?.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
        if (userId is null)
        {
            return;
        }

        var isMember = await db.ChannelMembers.AsNoTracking()
            .AnyAsync(m => m.ChannelId == channelId && m.UserId == userId);
        if (!isMember)
        {
            return;
        }

        await Clients.OthersInGroup(GroupName(channelId))
            .SendAsync("user:typing", channelId, userId, name);
    }

    /// <summary>Nome do grupo SignalR de uma nota colaborativa.</summary>
    /// <param name="noteId">Nota compartilhada.</param>
    public static string NoteGroupName(string noteId) => $"note:{noteId}";

    /// <summary>
    /// Entra no grupo de uma nota (exige leitura). Registra awareness e
    /// propaga o roster atualizado ao grupo.
    /// </summary>
    /// <param name="noteId">Nota a acompanhar.</param>
    public async Task JoinNote(string noteId)
    {
        var user = await CurrentUserAsync();
        var note = await db.Notes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == noteId);
        if (user is null || note is null
            || await access.LevelAsync(user, note.UserId, note.AccessGrantsJson)
                is AccessControlService.None)
        {
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, NoteGroupName(noteId));
        var members = NotePresenceMap.GetOrAdd(
            noteId, _ => new ConcurrentDictionary<string, NotePresenceEntry>());
        members[Context.ConnectionId] = new NotePresenceEntry(
            user.Id, user.Name, CursorColor(user.Id), 0);
        await BroadcastNotePresence(noteId);
    }

    /// <summary>Sai do grupo de uma nota e propaga o roster.</summary>
    /// <param name="noteId">Nota a deixar.</param>
    public async Task LeaveNote(string noteId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, NoteGroupName(noteId));
        if (NotePresenceMap.TryGetValue(noteId, out var members)
            && members.TryRemove(Context.ConnectionId, out _))
        {
            await BroadcastNotePresence(noteId);
        }
    }

    /// <summary>
    /// Aplica uma edição colaborativa (texto completo + versão vista).
    /// Last-write-wins: versão defasada ou sem write grant → rejeita com o
    /// estado atual; aceita → persiste e propaga aos demais do grupo.
    /// </summary>
    /// <param name="noteId">Nota editada.</param>
    /// <param name="text">Conteúdo completo proposto.</param>
    /// <param name="version"><c>UpdatedAt</c> que o cliente viu por último.</param>
    /// <returns>Nova versão (UpdatedAt) quando aceita; -1 quando rejeitada.</returns>
    public async Task<long> NoteUpdate(string noteId, string text, long version)
    {
        var user = await CurrentUserAsync();
        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == noteId);
        if (user is null || note is null)
        {
            return -1;
        }

        var level = await access.LevelAsync(user, note.UserId, note.AccessGrantsJson);
        var stale = note.UpdatedAt != version;
        if (level is not AccessControlService.Write || stale)
        {
            await Clients.Caller.SendAsync(
                "note:rejected", noteId, note.Content, note.UpdatedAt);
            return -1;
        }

        note.Content = text;
        note.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync();
        await Clients.OthersInGroup(NoteGroupName(noteId))
            .SendAsync("note:update", noteId, user.Id, text, note.UpdatedAt);
        return note.UpdatedAt;
    }

    /// <summary>Propaga a posição de cursor do usuário aos demais do grupo.</summary>
    /// <param name="noteId">Nota em edição.</param>
    /// <param name="cursor">Offset do cursor no texto.</param>
    public async Task NotePresence(string noteId, int cursor)
    {
        if (NotePresenceMap.TryGetValue(noteId, out var members)
            && members.TryGetValue(Context.ConnectionId, out var entry))
        {
            members[Context.ConnectionId] = entry with { Cursor = cursor };
            await Clients.OthersInGroup(NoteGroupName(noteId))
                .SendAsync("note:cursor", noteId, entry.UserId, entry.Name,
                    entry.Color, cursor);
        }
    }

    private async Task BroadcastNotePresence(string noteId)
    {
        var roster = NotePresenceMap.TryGetValue(noteId, out var members)
            ? members.Values.DistinctBy(m => m.UserId).ToArray()
            : [];
        await Clients.Group(NoteGroupName(noteId))
            .SendAsync("note:presence", noteId, roster);
    }

    private static string CursorColor(string userId)
    {
        var hash = userId.Aggregate(17, (acc, c) => acc * 31 + c);
        return CollabPalette[Math.Abs(hash) % CollabPalette.Length];
    }

    private async Task<User?> CurrentUserAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return userId is null
            ? null
            : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
    }

    /// <summary>Entrada de awareness de uma nota (roster + cursor).</summary>
    /// <param name="UserId">Usuário.</param>
    /// <param name="Name">Nome de exibição.</param>
    /// <param name="Color">Cor do cursor/presença.</param>
    /// <param name="Cursor">Offset do cursor no texto.</param>
    public sealed record NotePresenceEntry(
        string UserId, string Name, string Color, int Cursor);
}

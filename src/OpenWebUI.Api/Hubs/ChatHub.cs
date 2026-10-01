using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Hubs;

/// <summary>
/// Hub realtime de canais: entrega message:new, user:typing e presence
/// aos membros conectados. JWT chega via query access_token no handshake.
/// </summary>
[Authorize]
public class ChatHub(AppDbContext db) : Hub
{
    /// <summary>Conexões ativas por usuário (multi-aba).</summary>
    private static readonly ConcurrentDictionary<string, int> ConnectionsPerUser = new();

    /// <summary>Usuários online por canal.</summary>
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Presence = new();

    /// <summary>Nome do grupo SignalR de um canal.</summary>
    public static string GroupName(string channelId) => $"channel-{channelId}";

    /// <summary>Ids de canais onde o usuário está online.</summary>
    public static IReadOnlyList<string> OnlineUsers(string channelId) =>
        Presence.TryGetValue(channelId, out var set) ? set.Keys.ToArray() : [];

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
}

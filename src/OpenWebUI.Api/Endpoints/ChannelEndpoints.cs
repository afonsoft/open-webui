using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Hubs;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints REST de canais (/api/v1/channels) com difusão SignalR.</summary>
public static class ChannelEndpoints
{
    /// <summary>Mapeia o grupo /api/v1/channels.</summary>
    public static void MapChannelEndpoints(this WebApplication app)
    {
        var channels = app.MapGroup("/api/v1/channels").RequireAuthorization();
        channels.MapGet("/", ListChannelsAsync);
        channels.MapPost("/", CreateChannelAsync);
        channels.MapGet("/{id}", GetChannelAsync);
        channels.MapPut("/{id}", UpdateChannelAsync);
        channels.MapDelete("/{id}", DeleteChannelAsync);
        channels.MapGet("/{id}/messages", ListMessagesAsync);
        channels.MapPost("/{id}/messages", PostMessageAsync);
        channels.MapPost("/{id}/members", AddMembersAsync);
        channels.MapDelete("/{id}/members/{userId}", RemoveMemberAsync);
    }

    private static async Task<IResult> ListChannelsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var channels = await db.Channels.AsNoTracking()
            .Where(c => c.Members.Any(m => m.UserId == user.Id))
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Description,
                c.Type,
                c.CreatedAt,
                MemberCount = c.Members.Count(),
                MyRole = c.Members.Where(m => m.UserId == user.Id).Select(m => m.Role).First(),
            })
            .ToListAsync(ct);

        return Results.Ok(channels.Select(c => new ChannelResponse(
            c.Id, c.Name, c.Description, c.Type, c.MemberCount, c.MyRole, c.CreatedAt)));
    }

    private static async Task<IResult> CreateChannelAsync(
        [FromBody] CreateChannelRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { detail = "Nome do canal é obrigatório." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var channel = new Channel
        {
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            CreatedByUserId = user.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        channel.Members.Add(new ChannelMember
        {
            ChannelId = channel.Id, UserId = user.Id, Role = "admin", CreatedAt = now,
        });

        foreach (var memberId in (request.MemberIds ?? [])
                 .Where(id => id != user.Id).Distinct())
        {
            if (await db.Users.AnyAsync(u => u.Id == memberId, ct))
            {
                channel.Members.Add(new ChannelMember
                {
                    ChannelId = channel.Id, UserId = memberId, CreatedAt = now,
                });
            }
        }

        db.Channels.Add(channel);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new ChannelResponse(
            channel.Id, channel.Name, channel.Description, channel.Type,
            channel.Members.Count, "admin", channel.CreatedAt));
    }

    private static async Task<IResult> GetChannelAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, channel) = await LoadMembershipAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = "Canal não encontrado." });
        }

        var membership = await db.ChannelMembers.AsNoTracking()
            .Where(m => m.ChannelId == id)
            .Join(db.Users, m => m.UserId, u => u.Id,
                (m, u) => new ChannelMemberResponse(u.Id, u.Name, u.ProfileImageUrl, m.Role))
            .ToListAsync(ct);

        var myRole = membership.First(m => m.UserId == user.Id).Role;
        return Results.Ok(new ChannelDetailResponse(
            channel.Id, channel.Name, channel.Description, channel.Type,
            myRole, channel.CreatedAt, membership));
    }

    private static async Task<IResult> UpdateChannelAsync(
        string id, [FromBody] UpdateChannelRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, channel, role) = await LoadWithRoleAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = "Canal não encontrado." });
        }
        if (!IsChannelAdmin(user, role))
        {
            return Results.Forbid();
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            channel.Name = request.Name.Trim();
        }
        if (request.Description is not null)
        {
            channel.Description = request.Description.Trim();
        }
        channel.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    private static async Task<IResult> DeleteChannelAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, channel, role) = await LoadWithRoleAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = "Canal não encontrado." });
        }
        if (!IsChannelAdmin(user, role))
        {
            return Results.Forbid();
        }

        db.Channels.Remove(channel);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    private static async Task<IResult> ListMessagesAsync(
        string id, HttpContext http, AppDbContext db,
        [FromQuery] int skip = 0, [FromQuery] int take = 0, CancellationToken ct = default)
    {
        var (user, channel) = await LoadMembershipAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = "Canal não encontrado." });
        }

        var messages = await db.ChannelMessages.AsNoTracking()
            .Where(m => m.ChannelId == id)
            .OrderBy(m => m.CreatedAt)
            .Skip(Math.Max(0, skip))
            .Take(take is > 0 and <= 200 ? take : 50)
            .Select(m => new ChannelMessageResponse(
                m.Id, m.ChannelId, m.UserId, m.ModelId,
                m.UserId == null ? m.ModelId ?? "modelo" : m.User!.Name,
                m.User!.ProfileImageUrl,
                m.Content, m.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(messages);
    }

    private static async Task<IResult> PostMessageAsync(
        string id, [FromBody] CreateChannelMessageRequest request,
        HttpContext http, AppDbContext db,
        IHubContext<ChatHub> hub, IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var (user, channel) = await LoadMembershipAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = "Canal não encontrado." });
        }
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return Results.BadRequest(new { detail = "Conteúdo é obrigatório." });
        }

        var message = new ChannelMessage
        {
            ChannelId = id,
            UserId = user.Id,
            Content = request.Content,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        db.ChannelMessages.Add(message);
        channel.UpdatedAt = message.CreatedAt;
        await db.SaveChangesAsync(ct);

        var response = new ChannelMessageResponse(
            message.Id, id, user.Id, null, user.Name,
            user.ProfileImageUrl, message.Content, message.CreatedAt);
        await hub.Clients.Group(ChatHub.GroupName(id))
            .SendAsync("message:new", response, ct);

        var mention = ExtractModelMention(request.Content);
        if (mention is not null)
        {
            _ = ReplyWithModelAsync(scopeFactory, id, mention, request.Content);
        }

        return Results.Ok(response);
    }

    private static async Task<IResult> AddMembersAsync(
        string id, [FromBody] AddChannelMembersRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, channel, role) = await LoadWithRoleAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = "Canal não encontrado." });
        }
        if (!IsChannelAdmin(user, role))
        {
            return Results.Forbid();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var memberRole = request.Role == "admin" ? "admin" : "member";
        foreach (var memberId in (request.UserIds ?? []).Distinct())
        {
            var exists = await db.ChannelMembers
                .AnyAsync(m => m.ChannelId == id && m.UserId == memberId, ct);
            if (!exists && await db.Users.AnyAsync(u => u.Id == memberId, ct))
            {
                db.ChannelMembers.Add(new ChannelMember
                {
                    ChannelId = id, UserId = memberId, Role = memberRole, CreatedAt = now,
                });
            }
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    private static async Task<IResult> RemoveMemberAsync(
        string id, string userId,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, channel, role) = await LoadWithRoleAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = "Canal não encontrado." });
        }
        if (!IsChannelAdmin(user, role) && user.Id != userId)
        {
            return Results.Forbid();
        }

        var member = await db.ChannelMembers
            .FirstOrDefaultAsync(m => m.ChannelId == id && m.UserId == userId, ct);
        if (member is null)
        {
            return Results.NotFound(new { detail = "Membro não encontrado." });
        }

        db.ChannelMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    /// <summary>Extrai o identificador de modelo da primeira menção @modelo.</summary>
    private static string? ExtractModelMention(string content)
    {
        var match = Regex.Match(content, @"@([\w.:\-/]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Resolve a menção contra modelos conhecidos e posta a resposta do
    /// provedor no canal (ou mensagem de erro) via SignalR.
    /// </summary>
    private static async Task ReplyWithModelAsync(
        IServiceScopeFactory scopeFactory, string channelId,
        string mention, string prompt)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var providers = scope.ServiceProvider.GetRequiredService<ProviderService>();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<ChatHub>>();
        var ct = CancellationToken.None;

        var modelId = await ResolveModelAsync(db, providers, mention);
        string content;
        string? resolvedModel = modelId;
        if (modelId is null)
        {
            content = $"Modelo '@{mention}' não encontrado.";
            resolvedModel = mention;
        }
        else
        {
            try
            {
                content = await providers.CompleteAsync(
                    new ChatCompletionRequest(
                        modelId,
                        [new ChatCompletionMessage("user", prompt)],
                        Stream: false),
                    ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                content = $"Erro ao consultar o modelo '{modelId}': {ex.Message}";
            }
        }

        var message = new ChannelMessage
        {
            ChannelId = channelId,
            UserId = null,
            ModelId = resolvedModel,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        db.ChannelMessages.Add(message);
        await db.SaveChangesAsync(ct);

        await hub.Clients.Group(ChatHub.GroupName(channelId)).SendAsync(
            "message:new",
            new ChannelMessageResponse(
                message.Id, channelId, null, resolvedModel,
                resolvedModel ?? "modelo", null, content, message.CreatedAt),
            ct);
    }

    /// <summary>Resolve a menção contra modelos customizados e provedores.</summary>
    private static async Task<string?> ResolveModelAsync(
        AppDbContext db, ProviderService providers, string mention)
    {
        var custom = await db.ModelEntries.AsNoTracking()
            .FirstOrDefaultAsync(m =>
                m.IsActive && (m.Id == mention || m.Name == mention));
        if (custom is not null)
        {
            return custom.Id;
        }

        try
        {
            var models = await providers.ListModelsAsync();
            return models
                .FirstOrDefault(m =>
                    m.Id.Equals(mention, StringComparison.OrdinalIgnoreCase) ||
                    m.Name.Equals(mention, StringComparison.OrdinalIgnoreCase))
                ?.Id;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<(User? user, Channel? channel)> LoadMembershipAsync(
        HttpContext http, string channelId, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return (null, null);
        }

        var isMember = await db.ChannelMembers
            .AnyAsync(m => m.ChannelId == channelId && m.UserId == user.Id, ct);
        if (!isMember)
        {
            return (user, null);
        }

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, ct);
        return (user, channel);
    }

    private static async Task<(User? user, Channel? channel, string? role)> LoadWithRoleAsync(
        HttpContext http, string channelId, AppDbContext db, CancellationToken ct)
    {
        var (user, channel) = await LoadMembershipAsync(http, channelId, db, ct);
        if (user is null || channel is null)
        {
            return (user, channel, null);
        }

        var role = await db.ChannelMembers
            .Where(m => m.ChannelId == channelId && m.UserId == user.Id)
            .Select(m => m.Role)
            .FirstAsync(ct);
        return (user, channel, role);
    }

    private static bool IsChannelAdmin(User user, string? role) =>
        user.Role == "admin" || role == "admin";

    private static async Task<User?> CurrentUserAsync(
        HttpContext http, AppDbContext db, CancellationToken ct) =>
        await AuthEndpoints.FindUserAsync(http, db, ct);
}

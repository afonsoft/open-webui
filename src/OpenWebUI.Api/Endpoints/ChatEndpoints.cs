using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de chats, espelhando <c>/api/v1/chats</c> do Open WebUI.</summary>
public static class ChatEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia as rotas de chats.</summary>
    public static RouteGroupBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/chats").RequireAuthorization();

        group.MapGet("/", ListChatsAsync);
        group.MapGet("/list", ListChatsAsync);
        group.MapGet("/search", ListChatsAsync);
        group.MapPost("/", CreateChatAsync);
        group.MapPost("/new", CreateChatAsync);
        group.MapGet("/pinned", ListPinnedAsync);
        group.MapGet("/archived", ListArchivedAsync);
        group.MapGet("/archived/count", CountArchivedAsync);
        group.MapGet("/all/tags", ListAllTagsAsync);
        group.MapGet("/all/db", ExportAllAsync);
        group.MapGet("/shared", ListSharedAsync);
        group.MapGet("/share/{shareId}", GetSharedChatAsync).AllowAnonymous();
        group.MapPost("/import", ImportChatsAsync);
        group.MapPost("/archive/all", ArchiveAllAsync);
        group.MapPost("/unarchive/all", UnarchiveAllAsync);
        group.MapDelete("/", DeleteAllChatsAsync);
        group.MapGet("/folder/{folderId}", ListChatsAsync);
        group.MapPost("/tags", ListChatsByTagAsync);

        group.MapGet("/{id}", GetChatAsync);
        group.MapPost("/{id}", UpdateChatAsync);
        group.MapDelete("/{id}", DeleteChatAsync);
        group.MapPost("/{id}/pin", TogglePinAsync);
        group.MapGet("/{id}/pinned", GetPinnedAsync);
        group.MapPost("/{id}/archive", ToggleArchiveAsync);
        group.MapPost("/{id}/share", ShareChatAsync);
        group.MapDelete("/{id}/share", UnshareChatAsync);
        group.MapPost("/{id}/clone", CloneChatAsync);
        group.MapPost("/{id}/folder", SetFolderAsync);
        group.MapGet("/{id}/tags", GetTagsAsync);
        group.MapPost("/{id}/tags", SetTagsAsync);
        group.MapDelete("/{id}/tags", ClearTagsAsync);
        group.MapPost("/{id}/messages/{messageId}", UpdateMessageAsync);
        group.MapDelete("/{id}/messages/{messageId}", DeleteMessageAsync);

        return group;
    }

    private static async Task<IResult> ListChatsAsync(
        string? folderId,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct,
        string? query = null,
        bool includeFolders = false)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var chats = db.Chats.AsNoTracking().Where(c => c.UserId == user.Id && !c.Archived);

        if (!string.IsNullOrEmpty(folderId))
        {
            chats = chats.Where(c => c.FolderId == folderId);
        }
        else if (!includeFolders)
        {
            chats = chats.Where(c => c.FolderId == null);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLower();
            chats = chats.Where(c =>
                c.Title.ToLower().Contains(term)
                || c.Messages.Any(m => m.Content.ToLower().Contains(term)));
        }

        var list = await chats
            .OrderByDescending(c => c.Pinned)
            .ThenByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        return Results.Ok(list.Select(ToSummary).ToList());
    }

    private static async Task<IResult> ListPinnedAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var list = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id && c.Pinned && !c.Archived)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        return Results.Ok(list.Select(ToSummary).ToList());
    }

    private static async Task<IResult> ListArchivedAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var list = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id && c.Archived)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        return Results.Ok(list.Select(ToSummary).ToList());
    }

    private static async Task<IResult> CountArchivedAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var count = await db.Chats.CountAsync(c => c.UserId == user.Id && c.Archived, ct);
        return Results.Ok(new { count });
    }

    private static async Task<IResult> ListAllTagsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var tagJsons = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id)
            .Select(c => c.TagsJson)
            .ToListAsync(ct);

        var tags = tagJsons
            .SelectMany(t => JsonSerializer.Deserialize<List<string>>(t, JsonOptions) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();

        return Results.Ok(tags);
    }

    private static async Task<IResult> ListChatsByTagAsync(
        TagQueryRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var chats = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id && !c.Archived)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        var filtered = chats
            .Where(c => (JsonSerializer.Deserialize<List<string>>(c.TagsJson, JsonOptions) ?? [])
                .Any(t => request.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            .Select(ToSummary)
            .ToList();

        return Results.Ok(filtered);
    }

    private static async Task<IResult> ListSharedAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var list = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id && c.ShareId != null)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        return Results.Ok(list.Select(ToSummary).ToList());
    }

    private static async Task<IResult> GetSharedChatAsync(
        string shareId, AppDbContext db, CancellationToken ct)
    {
        var chat = await db.Chats.AsNoTracking()
            .Include(c => c.Messages.OrderBy(m => m.Position))
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.ShareId == shareId, ct);

        if (chat is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new
        {
            chat.Id,
            chat.Title,
            User = new { chat.User?.Name },
            Models = JsonSerializer.Deserialize<List<string>>(chat.ModelsJson, JsonOptions) ?? [],
            Messages = chat.Messages
                .OrderBy(m => m.Position)
                .Select(m => new ChatMessageModel(m.Id, m.Role, m.Content, m.Model, m.Timestamp))
                .ToList(),
            chat.CreatedAt,
            chat.UpdatedAt,
        });
    }

    private static async Task<IResult> ExportAllAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var chats = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id)
            .Include(c => c.Messages)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            chats = chats.Select(ToResponse).ToList(),
            exportedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
    }

    private static async Task<IResult> ImportChatsAsync(
        List<ChatUpsertRequest> request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var item in request)
        {
            var chat = new Chat
            {
                UserId = user.Id,
                Title = string.IsNullOrWhiteSpace(item.Title) ? "New Chat" : item.Title.Trim(),
                ModelsJson = JsonSerializer.Serialize(item.Models ?? [], JsonOptions),
                CreatedAt = now,
                UpdatedAt = now,
            };
            chat.Messages = MapMessages(item.Messages, chat.Id, now);
            db.Chats.Add(chat);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> CreateChatAsync(
        ChatUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var chat = new Chat
        {
            UserId = user.Id,
            Title = string.IsNullOrWhiteSpace(request.Title) ? "New Chat" : request.Title.Trim(),
            ModelsJson = JsonSerializer.Serialize(request.Models ?? [], JsonOptions),
            CreatedAt = now,
            UpdatedAt = now,
        };

        chat.Messages = MapMessages(request.Messages, chat.Id, now);

        db.Chats.Add(chat);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> GetChatAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct);
        return chat is null ? Results.NotFound() : Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> UpdateChatAsync(
        string id,
        ChatUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        chat.Title = string.IsNullOrWhiteSpace(request.Title) ? chat.Title : request.Title.Trim();
        chat.ModelsJson = JsonSerializer.Serialize(request.Models ?? [], JsonOptions);
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        db.ChatMessages.RemoveRange(chat.Messages);
        chat.Messages = MapMessages(request.Messages, chat.Id, chat.UpdatedAt);

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> DeleteChatAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        db.Chats.Remove(chat);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> DeleteAllChatsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        await db.Chats.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> TogglePinAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        chat.Pinned = !chat.Pinned;
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> GetPinnedAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct);
        return chat is null
            ? Results.NotFound()
            : Results.Ok(new { pinned = chat.Pinned });
    }

    private static async Task<IResult> ToggleArchiveAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        chat.Archived = !chat.Archived;
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> ArchiveAllAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.Chats
            .Where(c => c.UserId == user.Id && !c.Archived)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Archived, true)
                .SetProperty(c => c.UpdatedAt, now), ct);

        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> UnarchiveAllAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.Chats
            .Where(c => c.UserId == user.Id && c.Archived)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Archived, false)
                .SetProperty(c => c.UpdatedAt, now), ct);

        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> ShareChatAsync(
        string id, HttpContext http, AppDbContext db, PermissionService permissions,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        // RF-004: flag sharing.public_chats avaliada por união dos grupos.
        if (!await permissions.HasAsync(user!, PermissionService.SharingPublicChats, ct))
        {
            return Results.Forbid();
        }

        chat.ShareId ??= Guid.NewGuid().ToString("N")[..8];
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> UnshareChatAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        chat.ShareId = null;
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> CloneChatAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct);
        if (chat is null)
        {
            return Results.NotFound();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var clone = new Chat
        {
            UserId = user!.Id,
            Title = $"{chat.Title} (Clone)",
            ModelsJson = chat.ModelsJson,
            TagsJson = chat.TagsJson,
            CreatedAt = now,
            UpdatedAt = now,
        };
        clone.Messages = chat.Messages
            .OrderBy(m => m.Position)
            .Select((m, i) => new ChatMessage
            {
                ChatId = clone.Id,
                Role = m.Role,
                Content = m.Content,
                Model = m.Model,
                Position = i,
                Timestamp = m.Timestamp,
            })
            .ToList();

        db.Chats.Add(clone);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(clone));
    }

    private static async Task<IResult> SetFolderAsync(
        string id,
        SetFolderRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        if (!string.IsNullOrEmpty(request.FolderId))
        {
            var folderExists = await db.Folders
                .AnyAsync(f => f.Id == request.FolderId && f.UserId == user!.Id, ct);
            if (!folderExists)
            {
                return Results.NotFound(new { detail = "Pasta não encontrada." });
            }
        }

        chat.FolderId = string.IsNullOrEmpty(request.FolderId) ? null : request.FolderId;
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> GetTagsAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct);
        return chat is null
            ? Results.NotFound()
            : Results.Ok(JsonSerializer.Deserialize<List<string>>(chat.TagsJson, JsonOptions) ?? []);
    }

    private static async Task<IResult> SetTagsAsync(
        string id,
        TagUpdateRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        var tags = (request.Tags ?? [])
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        chat.TagsJson = JsonSerializer.Serialize(tags, JsonOptions);
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> ClearTagsAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        chat.TagsJson = "[]";
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> UpdateMessageAsync(
        string id,
        string messageId,
        MessageUpdateRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        var message = chat.Messages.FirstOrDefault(m => m.Id == messageId);
        if (message is null)
        {
            return Results.NotFound();
        }

        message.Content = request.Content;
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> DeleteMessageAsync(
        string id,
        string messageId,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        var index = chat.Messages.FindIndex(m => m.Id == messageId);
        if (index < 0)
        {
            return Results.NotFound();
        }

        // Remove a mensagem e tudo que veio depois dela (espelha o comportamento do original).
        var toRemove = chat.Messages
            .Where(m => m.Position >= chat.Messages[index].Position)
            .ToList();
        db.ChatMessages.RemoveRange(toRemove);
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(chat));
    }

    private static List<ChatMessage> MapMessages(
        IReadOnlyList<ChatMessageModel>? models, string chatId, long now) =>
        (models ?? [])
            .Select((m, i) => new ChatMessage
            {
                Id = string.IsNullOrEmpty(m.Id) ? Guid.NewGuid().ToString() : m.Id,
                ChatId = chatId,
                Role = m.Role,
                Content = m.Content,
                Model = m.Model,
                Position = i,
                Timestamp = m.Timestamp > 0 ? m.Timestamp : now,
            })
            .ToList();

    private static Task<Chat?> LoadChatAsync(
        string id, string? userId, AppDbContext db, CancellationToken ct, bool tracking = false)
    {
        if (userId is null)
        {
            return Task.FromResult<Chat?>(null);
        }

        var query = tracking ? db.Chats.AsQueryable() : db.Chats.AsNoTracking();
        return query
            .Include(c => c.Messages.OrderBy(m => m.Position))
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
    }

    private static ChatSummaryResponse ToSummary(Chat chat) => new(
        chat.Id,
        chat.Title,
        chat.Pinned,
        chat.FolderId,
        JsonSerializer.Deserialize<List<string>>(chat.TagsJson, JsonOptions) ?? [],
        chat.CreatedAt,
        chat.UpdatedAt);

    private static ChatResponse ToResponse(Chat chat) => new(
        chat.Id,
        chat.Title,
        JsonSerializer.Deserialize<List<string>>(chat.ModelsJson, JsonOptions) ?? [],
        chat.Messages
            .OrderBy(m => m.Position)
            .Select(m => new ChatMessageModel(m.Id, m.Role, m.Content, m.Model, m.Timestamp))
            .ToList(),
        chat.Pinned,
        chat.Archived,
        JsonSerializer.Deserialize<List<string>>(chat.TagsJson, JsonOptions) ?? [],
        chat.FolderId,
        chat.ShareId,
        chat.CreatedAt,
        chat.UpdatedAt);

    /// <summary>Filtro por tags.</summary>
    public sealed record TagQueryRequest(IReadOnlyList<string> Tags);

    /// <summary>Atribuição de pasta a um chat.</summary>
    public sealed record SetFolderRequest(string? FolderId);

    /// <summary>Atualização das tags de um chat.</summary>
    public sealed record TagUpdateRequest(IReadOnlyList<string>? Tags);
}

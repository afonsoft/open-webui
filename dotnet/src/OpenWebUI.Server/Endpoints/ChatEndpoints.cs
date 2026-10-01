using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Server.Data;
using OpenWebUI.Shared.Contracts;

namespace OpenWebUI.Server.Endpoints;

/// <summary>Endpoints de chats, espelhando <c>/api/v1/chats</c> do Open WebUI.</summary>
public static class ChatEndpoints
{
    /// <summary>Mapeia as rotas de chats.</summary>
    public static RouteGroupBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/chats").RequireAuthorization();

        group.MapGet("/", ListChatsAsync);
        group.MapPost("/", CreateChatAsync);
        group.MapGet("/{id}", GetChatAsync);
        group.MapPost("/{id}", UpdateChatAsync);
        group.MapDelete("/{id}", DeleteChatAsync);

        return group;
    }

    private static async Task<IResult> ListChatsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct, string? query = null)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var chats = db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id && !c.Archived);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLower();
            chats = chats.Where(c => c.Title.ToLower().Contains(term));
        }

        var list = await chats
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ChatSummaryResponse(c.Id, c.Title, c.CreatedAt, c.UpdatedAt))
            .ToListAsync(ct);

        return Results.Ok(list);
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
            ModelsJson = JsonSerializer.Serialize(request.Models ?? []),
            CreatedAt = now,
            UpdatedAt = now,
        };

        chat.Messages = (request.Messages ?? [])
            .Select((m, i) => new ChatMessage
            {
                Id = string.IsNullOrEmpty(m.Id) ? Guid.NewGuid().ToString() : m.Id,
                ChatId = chat.Id,
                Role = m.Role,
                Content = m.Content,
                Model = m.Model,
                Position = i,
                Timestamp = m.Timestamp > 0 ? m.Timestamp : now,
            })
            .ToList();

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
        chat.ModelsJson = JsonSerializer.Serialize(request.Models ?? []);
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        db.ChatMessages.RemoveRange(chat.Messages);
        chat.Messages = (request.Messages ?? [])
            .Select((m, i) => new ChatMessage
            {
                Id = string.IsNullOrEmpty(m.Id) ? Guid.NewGuid().ToString() : m.Id,
                ChatId = chat.Id,
                Role = m.Role,
                Content = m.Content,
                Model = m.Model,
                Position = i,
                Timestamp = m.Timestamp > 0 ? m.Timestamp : chat.UpdatedAt,
            })
            .ToList();

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
        return Results.Ok(new { success = true });
    }

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

    private static ChatResponse ToResponse(Chat chat) => new(
        chat.Id,
        chat.Title,
        JsonSerializer.Deserialize<IReadOnlyList<string>>(chat.ModelsJson) ?? [],
        chat.Messages
            .OrderBy(m => m.Position)
            .Select(m => new ChatMessageModel(m.Id, m.Role, m.Content, m.Model, m.Timestamp))
            .ToList(),
        chat.CreatedAt,
        chat.UpdatedAt);
}

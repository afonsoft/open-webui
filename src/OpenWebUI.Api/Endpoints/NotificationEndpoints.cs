using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de webhooks de notificação (por usuário e globais/admin).</summary>
public static class NotificationEndpoints
{
    /// <summary>Mapeia as rotas de notifications.</summary>
    public static RouteGroupBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/notifications").RequireAuthorization();

        // Nota: lambdas precisam de 2+ parâmetros para não casar com o overload
        // RequestDelegate, que descarta o IResult retornado (ASP0016).
        group.MapGet("/webhook", (HttpContext http, AppDbContext db) => GetAsync(http, db, false));
        group.MapPost("/webhook", (HttpContext http, AppDbContext db) => SaveAsync(http, db, false));
        group.MapDelete("/webhook", (HttpContext http, AppDbContext db) => DeleteAsync(http, db, false));
        group.MapPost("/webhook/test",
            (HttpContext http, AppDbContext db, NotificationService notifications) =>
                TestAsync(http, db, notifications, false));

        group.MapGet("/admin/webhook", (HttpContext http, AppDbContext db) => GetAsync(http, db, true));
        group.MapPost("/admin/webhook", (HttpContext http, AppDbContext db) => SaveAsync(http, db, true));
        group.MapDelete("/admin/webhook", (HttpContext http, AppDbContext db) => DeleteAsync(http, db, true));
        group.MapPost("/admin/webhook/test",
            (HttpContext http, AppDbContext db, NotificationService notifications) =>
                TestAsync(http, db, notifications, true));

        return group;
    }

    private static async Task<IResult> GetAsync(HttpContext http, AppDbContext db, bool global)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();
        if (global && user.Role != UserRoles.Admin) return Results.Forbid();

        var webhook = await FindAsync(db, user, global, http.RequestAborted);
        return webhook is null ? Results.NotFound() : Results.Ok(Map(webhook));
    }

    private static async Task<IResult> SaveAsync(HttpContext http, AppDbContext db, bool global)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();
        if (global && user.Role != UserRoles.Admin) return Results.Forbid();

        var request = await JsonSerializer.DeserializeAsync<NotificationWebhookRequest>(
            http.Request.Body, JsonSerializerOptions.Web, http.RequestAborted);
        if (request is null || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return Results.BadRequest(new { detail = "URL http/https válida obrigatória." });
        }

        var webhook = await FindAsync(db, user, global, http.RequestAborted);
        if (webhook is null)
        {
            webhook = new NotificationWebhook
            {
                OwnerId = global ? null : user.Id,
                Secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                    .ToLowerInvariant(),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            db.NotificationWebhooks.Add(webhook);
        }
        webhook.Url = request.Url;
        webhook.Events = string.Join(",", request.Events);
        webhook.Enabled = request.Enabled;

        await db.SaveChangesAsync();
        return Results.Ok(Map(webhook));
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, AppDbContext db, bool global)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();
        if (global && user.Role != UserRoles.Admin) return Results.Forbid();

        var webhook = await FindAsync(db, user, global, http.RequestAborted);
        if (webhook is null) return Results.NotFound();
        db.NotificationWebhooks.Remove(webhook);
        await db.SaveChangesAsync();
        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> TestAsync(
        HttpContext http, AppDbContext db, NotificationService notifications, bool global)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();
        if (global && user.Role != UserRoles.Admin) return Results.Forbid();

        var webhook = await FindAsync(db, user, global, http.RequestAborted);
        if (webhook is null) return Results.NotFound();

        var body = JsonSerializer.Serialize(new { @event = "test", data = new { }, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        var status = await notifications.SendSafelyAsync(webhook, "test", body);
        return Results.Ok(new WebhookTestResponse(status, status is >= 200 and < 300));
    }

    private static Task<NotificationWebhook?> FindAsync(AppDbContext db, User user, bool global, CancellationToken ct) =>
        db.NotificationWebhooks.FirstOrDefaultAsync(
            w => global ? w.OwnerId == null : w.OwnerId == user.Id, ct);

    private static NotificationWebhookResponse Map(NotificationWebhook w) => new(
        w.Id, w.Url,
        w.Events.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        w.Enabled);
}

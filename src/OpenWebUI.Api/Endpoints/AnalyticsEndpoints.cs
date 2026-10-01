using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Dashboard de analytics admin (/api/v1/analytics) — só contagens, nunca conteúdo.</summary>
public static class AnalyticsEndpoints
{
    /// <summary>Mapeia GET /api/v1/analytics?days=.</summary>
    public static void MapAnalyticsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/analytics").RequireAuthorization();
        group.MapGet("/", GetAsync);
    }

    private static async Task<IResult> GetAsync(
        HttpContext http, AppDbContext db, int days = 30, CancellationToken ct = default)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (user.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }

        days = Math.Clamp(days, 1, 365);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var since = now - (long)days * 86400;

        var messageBuckets = await db.ChatMessages.AsNoTracking()
            .Where(m => m.Timestamp >= since)
            .GroupBy(m => m.Timestamp / 86400)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var chatBuckets = await db.Chats.AsNoTracking()
            .Where(c => c.CreatedAt >= since)
            .GroupBy(c => c.CreatedAt / 86400)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var modelBuckets = await db.ChatMessages.AsNoTracking()
            .Where(m => m.Timestamp >= since && m.Model != null && m.Role == "assistant")
            .GroupBy(m => m.Model!)
            .Select(g => new { Model = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var models = modelBuckets
            .OrderByDescending(m => m.Count)
            .Take(10)
            .Select(m => new ModelUsageCount(m.Model, m.Count))
            .ToList();

        var totalUsers = await db.Users.CountAsync(ct);
        var newUsers = await db.Users.CountAsync(u => u.CreatedAt >= since, ct);
        var positive = await db.Feedbacks.CountAsync(f => f.CreatedAt >= since && f.Rating > 0, ct);
        var negative = await db.Feedbacks.CountAsync(f => f.CreatedAt >= since && f.Rating < 0, ct);

        return Results.Ok(new AnalyticsResponse(
            new AnalyticsUsers(totalUsers, newUsers),
            new AnalyticsSeries(
                FillDays(messageBuckets.ToDictionary(b => b.Day, b => b.Count), days),
                FillDays(chatBuckets.ToDictionary(b => b.Day, b => b.Count), days)),
            models,
            new AnalyticsEvaluations(positive, negative)));
    }

    /// <summary>Preenche dias sem dados com zero (série contínua).</summary>
    private static List<DayCount> FillDays(Dictionary<long, int> buckets, int days)
    {
        var today = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86400;
        var series = new List<DayCount>(days);
        for (var i = days - 1; i >= 0; i--)
        {
            var day = today - i;
            series.Add(new DayCount(
                DateTimeOffset.FromUnixTimeSeconds(day * 86400).ToString("yyyy-MM-dd"),
                buckets.GetValueOrDefault(day)));
        }
        return series;
    }
}

using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de avaliações (feedback de mensagens), espelhando <c>/api/v1/evaluations</c>.</summary>
public static class EvaluationEndpoints
{
    /// <summary>Mapeia as rotas de avaliações.</summary>
    public static RouteGroupBuilder MapEvaluationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/evaluations").RequireAuthorization();

        group.MapPost("/feedback", SaveFeedbackAsync);
        group.MapGet("/feedback/{id}", GetFeedbackAsync);
        group.MapPost("/feedback/{id}", SaveFeedbackByIdAsync);
        group.MapDelete("/feedback/{id}", DeleteFeedbackAsync);
        group.MapGet("/feedbacks/user", ListUserFeedbacksAsync);
        group.MapGet("/feedbacks/list", ListAllFeedbacksAsync);
        group.MapGet("/feedbacks/all/export", ExportAllFeedbacksAsync);
        group.MapDelete("/feedbacks/all", DeleteAllFeedbacksAsync);

        return group;
    }

    private static async Task<IResult> SaveFeedbackAsync(
        FeedbackUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (request.Rating is not (1 or -1))
        {
            return Results.BadRequest(new { detail = "Avaliação deve ser 1 ou -1." });
        }

        var existing = await db.Feedbacks
            .FirstOrDefaultAsync(f => f.UserId == user.Id && f.MessageId == request.MessageId, ct);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (existing is null)
        {
            var feedback = new Feedback
            {
                UserId = user.Id,
                ChatId = request.ChatId,
                MessageId = request.MessageId,
                ModelId = request.ModelId,
                Rating = request.Rating,
                Reason = request.Reason,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Feedbacks.Add(feedback);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(feedback));
        }

        existing.Rating = request.Rating;
        existing.Reason = request.Reason;
        existing.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(existing));
    }

    private static async Task<IResult> GetFeedbackAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var feedback = await db.Feedbacks.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id && f.UserId == user!.Id, ct);
        return feedback is null ? Results.NotFound() : Results.Ok(ToResponse(feedback));
    }

    private static async Task<IResult> SaveFeedbackByIdAsync(
        string id,
        FeedbackUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        return await SaveFeedbackAsync(request with { }, http, db, ct);
    }

    private static async Task<IResult> DeleteFeedbackAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var feedback = await db.Feedbacks
            .FirstOrDefaultAsync(f => f.Id == id && f.UserId == user!.Id, ct);
        if (feedback is null)
        {
            return Results.NotFound();
        }

        db.Feedbacks.Remove(feedback);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> ListUserFeedbacksAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var feedbacks = await db.Feedbacks.AsNoTracking()
            .Where(f => f.UserId == user.Id)
            .OrderByDescending(f => f.UpdatedAt)
            .ToListAsync(ct);

        return Results.Ok(feedbacks.Select(ToResponse).ToList());
    }

    private static async Task<IResult> ListAllFeedbacksAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var feedbacks = await db.Feedbacks.AsNoTracking()
            .OrderByDescending(f => f.UpdatedAt)
            .ToListAsync(ct);

        return Results.Ok(feedbacks.Select(ToResponse).ToList());
    }

    private static async Task<IResult> ExportAllFeedbacksAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var feedbacks = await db.Feedbacks.AsNoTracking().ToListAsync(ct);
        return Results.Ok(feedbacks.Select(ToResponse).ToList());
    }

    private static async Task<IResult> DeleteAllFeedbacksAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        await db.Feedbacks.Where(f => f.UserId == user.Id).ExecuteDeleteAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static FeedbackResponse ToResponse(Feedback f) =>
        new(f.Id, f.ChatId, f.MessageId, f.ModelId, f.Rating, f.Reason, f.CreatedAt);
}

using Microsoft.EntityFrameworkCore;
using OpenWebUI.Server.Data;
using OpenWebUI.Shared.Contracts;

namespace OpenWebUI.Server.Endpoints;

/// <summary>Endpoints de modelos personalizados, espelhando <c>/api/v1/models</c> do Open WebUI.</summary>
public static class ModelEndpoints
{
    /// <summary>Mapeia as rotas de modelos do workspace.</summary>
    public static RouteGroupBuilder MapModelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/models").RequireAuthorization();

        group.MapGet("/", ListModelsAsync);
        group.MapGet("/list", ListModelsAsync);
        group.MapPost("/create", CreateModelAsync);
        group.MapGet("/model", GetModelByQueryAsync);
        group.MapPost("/model/update", UpdateModelAsync);
        group.MapPost("/model/delete", DeleteModelAsync);
        group.MapPost("/model/toggle", ToggleModelAsync);
        group.MapGet("/export", ExportModelsAsync);
        group.MapPost("/import", ImportModelsAsync);

        return group;
    }

    private static async Task<IResult> ListModelsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var models = await db.ModelEntries.AsNoTracking()
            .Where(m => m.UserId == user.Id || m.UserId == "public")
            .OrderBy(m => m.Name)
            .ToListAsync(ct);

        return Results.Ok(models.Select(ToResponse).ToList());
    }

    private static async Task<IResult> CreateModelAsync(
        ModelEntryUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.BaseModelId))
        {
            return Results.BadRequest(new { detail = "Nome e modelo base são obrigatórios." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entry = new ModelEntry
        {
            UserId = user.Id,
            Name = request.Name.Trim(),
            BaseModelId = request.BaseModelId.Trim(),
            SystemPrompt = request.SystemPrompt,
            ParamsJson = request.ParamsJson,
            ProfileImageUrl = request.ProfileImageUrl,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ModelEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(entry));
    }

    private static async Task<IResult> GetModelByQueryAsync(
        HttpContext http, AppDbContext db, CancellationToken ct, string? id = null)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var model = await db.ModelEntries.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id && (m.UserId == user!.Id || m.UserId == "public"), ct);
        return model is null ? Results.NotFound() : Results.Ok(ToResponse(model));
    }

    private static async Task<IResult> UpdateModelAsync(
        ModelUpdateRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var model = await db.ModelEntries
            .FirstOrDefaultAsync(m => m.Id == request.Id && m.UserId == user!.Id, ct);
        if (model is null)
        {
            return Results.NotFound();
        }

        model.Name = string.IsNullOrWhiteSpace(request.Name) ? model.Name : request.Name.Trim();
        model.BaseModelId = string.IsNullOrWhiteSpace(request.BaseModelId) ? model.BaseModelId : request.BaseModelId.Trim();
        model.SystemPrompt = request.SystemPrompt;
        model.ParamsJson = request.ParamsJson;
        model.ProfileImageUrl = request.ProfileImageUrl;
        model.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(model));
    }

    private static async Task<IResult> ToggleModelAsync(
        ToggleRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var model = await db.ModelEntries
            .FirstOrDefaultAsync(m => m.Id == request.Id && m.UserId == user!.Id, ct);
        if (model is null)
        {
            return Results.NotFound();
        }

        model.IsActive = !model.IsActive;
        model.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(model));
    }

    private static async Task<IResult> DeleteModelAsync(
        ToggleRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var model = await db.ModelEntries
            .FirstOrDefaultAsync(m => m.Id == request.Id && m.UserId == user!.Id, ct);
        if (model is null)
        {
            return Results.NotFound();
        }

        db.ModelEntries.Remove(model);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> ExportModelsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var models = await db.ModelEntries.AsNoTracking()
            .Where(m => m.UserId == user.Id)
            .ToListAsync(ct);

        return Results.Ok(models.Select(ToResponse).ToList());
    }

    private static async Task<IResult> ImportModelsAsync(
        List<ModelEntryUpsertRequest> request,
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
            db.ModelEntries.Add(new ModelEntry
            {
                UserId = user.Id,
                Name = item.Name.Trim(),
                BaseModelId = item.BaseModelId.Trim(),
                SystemPrompt = item.SystemPrompt,
                ParamsJson = item.ParamsJson,
                ProfileImageUrl = item.ProfileImageUrl,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    internal static ModelEntryResponse ToResponse(ModelEntry m) => new(
        m.Id, m.Name, m.BaseModelId, m.SystemPrompt, m.ParamsJson, m.ProfileImageUrl,
        m.IsActive, m.CreatedAt, m.UpdatedAt);

    /// <summary>Referência a um modelo por id (toggle/delete).</summary>
    public sealed record ToggleRequest(string Id);

    /// <summary>Atualização de modelo personalizado.</summary>
    public sealed record ModelUpdateRequest(
        string Id, string? Name, string? BaseModelId, string? SystemPrompt,
        string? ParamsJson, string? ProfileImageUrl);
}

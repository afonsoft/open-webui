using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints CRUD de tools externas (/api/v1/tools).</summary>
public static class ToolEndpoints
{
    /// <summary>Mapeia o grupo /api/v1/tools.</summary>
    public static void MapToolEndpoints(this WebApplication app)
    {
        var tools = app.MapGroup("/api/v1/tools").RequireAuthorization()
            .RequirePermission(PermissionService.WorkspaceTools);
        tools.MapGet("/", ListAsync);
        tools.MapPost("/", CreateAsync);
        tools.MapPut("/{id}", UpdateAsync);
        tools.MapDelete("/{id}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var tools = await db.Tools.AsNoTracking()
            .Where(t => t.UserId == user.Id)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
        return Results.Ok(tools.Select(ToResponse));
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] ToolUpsertRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (ValidateSpec(request.SpecJson) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }
        var hasCode = !string.IsNullOrWhiteSpace(request.Code);
        if (hasCode && !request.Code!.Contains("class Tools", StringComparison.Ordinal))
        {
            return Results.BadRequest(new { detail = "O código da tool precisa definir a classe 'Tools'." });
        }
        if (string.IsNullOrWhiteSpace(request.Url) && !hasCode)
        {
            return Results.BadRequest(new { detail = "Informe a URL de execução ou o código Python da tool." });
        }
        if (!string.IsNullOrWhiteSpace(request.Url) &&
            (!Uri.TryCreate(request.Url, UriKind.Absolute, out _) ||
             request.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) is false &&
             !request.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return Results.BadRequest(new { detail = "URL de execução inválida (deve ser http(s))." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tool = new Tool
        {
            UserId = user.Id,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            SpecJson = request.SpecJson,
            Url = request.Url?.Trim() ?? string.Empty,
            Code = hasCode ? request.Code : null,
            Enabled = request.Enabled,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Tools.Add(tool);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(tool));
    }

    private static async Task<IResult> UpdateAsync(
        string id, [FromBody] ToolUpsertRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var tool = await db.Tools.FirstOrDefaultAsync(
            t => t.Id == id && t.UserId == user.Id, ct);
        if (tool is null)
        {
            return Results.NotFound(new { detail = "Tool não encontrada." });
        }
        if (ValidateSpec(request.SpecJson) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }

        tool.Name = request.Name.Trim();
        tool.Description = request.Description?.Trim();
        tool.SpecJson = request.SpecJson;
        if (!string.IsNullOrWhiteSpace(request.Code))
        {
            if (!request.Code.Contains("class Tools", StringComparison.Ordinal))
            {
                return Results.BadRequest(new { detail = "O código da tool precisa definir a classe 'Tools'." });
            }
            tool.Code = request.Code;
        }
        else if (request.Code is not null)
        {
            tool.Code = null; // string vazia enviada explicitamente remove o código
        }
        if (!string.IsNullOrWhiteSpace(request.Url))
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out _))
            {
                return Results.BadRequest(new { detail = "URL de execução inválida." });
            }
            tool.Url = request.Url.Trim();
        }
        if (string.IsNullOrWhiteSpace(tool.Url) && string.IsNullOrWhiteSpace(tool.Code))
        {
            return Results.BadRequest(new { detail = "A tool precisa de URL de execução ou código Python." });
        }
        tool.Enabled = request.Enabled;
        tool.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(tool));
    }

    private static async Task<IResult> DeleteAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var tool = await db.Tools.FirstOrDefaultAsync(
            t => t.Id == id && t.UserId == user.Id, ct);
        if (tool is null)
        {
            return Results.NotFound(new { detail = "Tool não encontrada." });
        }

        db.Tools.Remove(tool);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    /// <summary>Valida o spec da função: JSON com function.name.</summary>
    internal static string? ValidateSpec(string specJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(specJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("function", out var fn) ||
                !fn.TryGetProperty("name", out var name) ||
                string.IsNullOrWhiteSpace(name.GetString()))
            {
                return "Spec precisa de \"function.name\".";
            }
            return null;
        }
        catch (JsonException)
        {
            return "Spec não é um JSON válido.";
        }
    }

    private static ToolResponse ToResponse(Tool t) =>
        new(t.Id, t.Name, t.Description, t.SpecJson, t.Code, t.Enabled, t.CreatedAt);
}

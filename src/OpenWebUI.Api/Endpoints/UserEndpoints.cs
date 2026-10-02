using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de usuários, espelhando <c>/api/v1/users</c> do Open WebUI.</summary>
public static class UserEndpoints
{
    /// <summary>Mapeia as rotas de usuários.</summary>
    public static RouteGroupBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/users").RequireAuthorization();

        group.MapGet("/", ListUsersAsync);
        group.MapGet("/all", ListAllUsersAsync);
        group.MapGet("/search", SearchUsersAsync);
        group.MapGet("/active", ListActiveAsync);
        group.MapGet("/permissions", GetPermissionsAsync);
        group.MapGet("/user/settings", GetUserSettingsAsync);
        group.MapPost("/user/settings", UpdateUserSettingsAsync);
        group.MapPost("/user/settings/update", UpdateUserSettingsAsync);
        group.MapGet("/{id}", GetUserAsync);
        group.MapPost("/{id}/update", UpdateUserAsync);
        group.MapPost("/{id}/update/role", UpdateUserRoleAsync);
        group.MapGet("/{id}/oauth/sessions", ListOAuthSessionsAsync);
        group.MapDelete("/{id}/oauth/sessions/{sessionId}", RevokeOAuthSessionAsync);
        group.MapDelete("/{id}", DeleteUserAsync);

        return group;
    }

    private static async Task<IResult> ListUsersAsync(
        HttpContext http, AppDbContext db, CancellationToken ct,
        string? query = null, string? filter = null,
        string? order_by = null, string? direction = null,
        int page = 1, int per_page = 50)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var users = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLowerInvariant();
            users = users.Where(u => u.Name.ToLower().Contains(term) || u.Email.Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var role = filter.Trim().ToLowerInvariant();
            users = users.Where(u => u.Role == role);
        }

        var ascending = string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase);
        users = order_by?.ToLowerInvariant() switch
        {
            "name" => ascending ? users.OrderBy(u => u.Name) : users.OrderByDescending(u => u.Name),
            "email" => ascending ? users.OrderBy(u => u.Email) : users.OrderByDescending(u => u.Email),
            "role" => ascending ? users.OrderBy(u => u.Role) : users.OrderByDescending(u => u.Role),
            "updated_at" or "last_active_at" => ascending
                ? users.OrderBy(u => u.UpdatedAt)
                : users.OrderByDescending(u => u.UpdatedAt),
            _ => ascending ? users.OrderBy(u => u.CreatedAt) : users.OrderByDescending(u => u.CreatedAt),
        };

        var total = await users.CountAsync(ct);
        page = Math.Max(1, page);
        per_page = Math.Clamp(per_page, 1, 100);
        var list = await users
            .Skip((page - 1) * per_page)
            .Take(per_page)
            .Select(u => new AdminUserResponse(
                u.Id, u.Name, u.Email, u.Role, u.ProfileImageUrl,
                u.CreatedAt, u.LastActiveAt ?? u.UpdatedAt))
            .ToListAsync(ct);

        return Results.Ok(new { users = list, total, page });
    }

    private static IResult ListActiveAsync(HttpContext http)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        return Results.Ok(new { ids = Hubs.ChatHub.ConnectedUserIds() });
    }

    private static async Task<IResult> ListAllUsersAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var users = await db.Users.AsNoTracking()
            .Select(u => AuthEndpoints.ToResponse(u))
            .ToListAsync(ct);

        return Results.Ok(users);
    }

    private static async Task<IResult> SearchUsersAsync(
        HttpContext http, AppDbContext db, CancellationToken ct, string? query = null)
    {
        var users = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLower();
            users = users.Where(u => u.Name.ToLower().Contains(term) || u.Email.Contains(term));
        }

        var list = await users
            .Select(u => new { u.Id, u.Name, u.Email, u.ProfileImageUrl })
            .Take(20)
            .ToListAsync(ct);

        return Results.Ok(list);
    }

    private static IResult GetPermissionsAsync() => Results.Ok(new
    {
        workspace = new
        {
            models = true,
            knowledge = true,
            prompts = true,
            tools = true,
        },
        sharing = new { public_models = true, public_knowledge = true, public_prompts = true, public_tools = true },
        chat = new
        {
            controls = true,
            valves = true,
            system_prompt = true,
            @params = true,
            file_upload = true,
            delete = true,
            edit = true,
            share = true,
            export = true,
            stt = true,
            tts = true,
            call = true,
            multiple_models = true,
            temporary = true,
            temporary_enforced = false,
        },
        features = new { direct_tool_servers = false, web_search = false, image_generation = false, code_interpreter = false, notes = true },
    });

    private static async Task<IResult> GetUserSettingsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
            user.SettingsJson) ?? [];
        return Results.Ok(settings);
    }

    private static async Task<IResult> UpdateUserSettingsAsync(
        System.Text.Json.JsonElement request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        user.SettingsJson = request.GetRawText();
        user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);

        return Results.Ok(request);
    }

    private static async Task<IResult> GetUserAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        return user is null ? Results.NotFound() : Results.Ok(AuthEndpoints.ToResponse(user));
    }

    private static async Task<IResult> UpdateUserAsync(
        string id,
        AdminUpdateUserRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            user.Name = request.Name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.Role)
            && request.Role is UserRoles.Admin or UserRoles.User or UserRoles.Pending)
        {
            user.Role = request.Role;
        }

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>()
                .HashPassword(user, request.Password);
        }

        if (request.ProfileImageUrl is not null)
        {
            user.ProfileImageUrl = request.ProfileImageUrl;
        }

        user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(AuthEndpoints.ToResponse(user));
    }

    private static async Task<IResult> UpdateUserRoleAsync(
        string id,
        UpdateUserRoleRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }
        if (request.Role is not (UserRoles.Admin or UserRoles.User or UserRoles.Pending))
        {
            return Results.BadRequest(new { detail = "Papel inválido." });
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        user.Role = request.Role;
        user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(AuthEndpoints.ToResponse(user));
    }

    private static async Task<IResult> ListOAuthSessionsAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var selfId = http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (selfId != id && !http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var sessions = await db.OAuthAccounts.AsNoTracking()
            .Where(a => a.UserId == id)
            .Select(a => new { a.Id, a.Provider, a.ProviderAccountId, a.Email, a.CreatedAt })
            .ToListAsync(ct);
        return Results.Ok(new { sessions });
    }

    private static async Task<IResult> RevokeOAuthSessionAsync(
        string id, string sessionId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var selfId = http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (selfId != id && !http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var session = await db.OAuthAccounts
            .FirstOrDefaultAsync(a => a.Id == sessionId && a.UserId == id, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        db.OAuthAccounts.Remove(session);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> DeleteUserAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var selfId = http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (id == selfId)
        {
            return Results.BadRequest(new { detail = "Não é possível excluir a própria conta." });
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }
}

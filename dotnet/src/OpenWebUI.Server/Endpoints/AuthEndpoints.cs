using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Server.Data;
using OpenWebUI.Server.Services;
using OpenWebUI.Shared.Contracts;

namespace OpenWebUI.Server.Endpoints;

/// <summary>Endpoints de autenticação, espelhando <c>/api/v1/auths</c> do Open WebUI.</summary>
public static class AuthEndpoints
{
    /// <summary>Mapeia as rotas de autenticação.</summary>
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auths");

        group.MapPost("/signup", SignUpAsync).AllowAnonymous();
        group.MapPost("/signin", SignInAsync).AllowAnonymous();
        group.MapGet("/", GetCurrentUserAsync).RequireAuthorization();
        group.MapPut("/profile", UpdateProfileAsync).RequireAuthorization();

        return group;
    }

    private static async Task<IResult> SignUpAsync(
        SignUpRequest request,
        AppDbContext db,
        JwtTokenService tokens,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.BadRequest(new { detail = "Nome, e-mail e senha são obrigatórios." });
        }

        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return Results.BadRequest(new { detail = "E-mail já cadastrado." });
        }

        var isFirstUser = !await db.Users.AnyAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var user = new User
        {
            Name = request.Name.Trim(),
            Email = email,
            Role = isFirstUser ? UserRoles.Admin : UserRoles.User,
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, request.Password);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        return await IssueAuthResponseAsync(user, tokens, ct);
    }

    private static async Task<IResult> SignInAsync(
        SignInRequest request,
        AppDbContext db,
        JwtTokenService tokens,
        CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            return Results.BadRequest(new { detail = "Credenciais inválidas." });
        }

        var result = new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            return Results.BadRequest(new { detail = "Credenciais inválidas." });
        }

        if (user.Role == UserRoles.Pending)
        {
            return Results.BadRequest(new { detail = "Conta aguardando aprovação do administrador." });
        }

        return await IssueAuthResponseAsync(user, tokens, ct);
    }

    private static async Task<IResult> GetCurrentUserAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await FindUserAsync(http, db, ct);
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> UpdateProfileAsync(
        UpdateProfileRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            user.Name = request.Name.Trim();
        }

        user.ProfileImageUrl = request.ProfileImageUrl;
        user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(user));
    }

    internal static async Task<User?> FindUserAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var id = http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return id is null ? null : await db.Users.FindAsync([id], ct);
    }

    internal static UserResponse ToResponse(User user) =>
        new(user.Id, user.Name, user.Email, user.Role);

    private static async Task<IResult> IssueAuthResponseAsync(
        User user, JwtTokenService tokens, CancellationToken ct)
    {
        var (token, expires) = await tokens.CreateTokenAsync(user, ct);
        return Results.Ok(new AuthResponse(token, "Bearer", expires, ToResponse(user)));
    }
}

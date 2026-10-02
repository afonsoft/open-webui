using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints utilitários — gravatar e formatação de código.</summary>
public static class UtilsEndpoints
{
    /// <summary>Mapeia as rotas de utils.</summary>
    public static RouteGroupBuilder MapUtilsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/utils").RequireAuthorization();

        // Nota: lambdas precisam de 2+ parâmetros para não casar com o overload
        // RequestDelegate, que descarta o IResult retornado (ASP0016).
        group.MapGet("/gravatar",
            (HttpContext http, AppDbContext db) => GetGravatarAsync(http, db));
        group.MapPost("/code/format",
            (HttpContext http, AppDbContext db) => FormatCodeAsync(http, db));

        return group;
    }

    /// <summary>Retorna a URL de gravatar do e-mail — SHA256 do e-mail normalizado,
    /// igual ao upstream (<c>get_gravatar_url</c>). Nenhuma chamada externa é feita.</summary>
    private static async Task<IResult> GetGravatarAsync(HttpContext http, AppDbContext db)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();

        var email = http.Request.Query["email"].ToString().Trim().ToLowerInvariant();
        if (email.Length == 0)
        {
            return Results.BadRequest(new { detail = "Parâmetro email obrigatório." });
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email)))
            .ToLowerInvariant();
        return Results.Ok($"https://www.gravatar.com/avatar/{hash}?d=mp");
    }

    /// <summary>Formata código server-side (admin-only, como no upstream). Somente
    /// <c>json</c> tem formatador interno; demais linguagens retornam 501 enquanto
    /// não houver executor externo configurado.</summary>
    private static async Task<IResult> FormatCodeAsync(HttpContext http, AppDbContext db)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();
        if (user.Role != UserRoles.Admin) return Results.Forbid();

        CodeFormatRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<CodeFormatRequest>(
                http.Request.Body, JsonSerializerOptions.Web, http.RequestAborted);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null)
        {
            return Results.BadRequest(new { detail = "Corpo JSON inválido." });
        }

        var language = (request.Language ?? "json").Trim().ToLowerInvariant();
        if (language.Length == 0)
        {
            language = "json";
        }

        if (language != "json")
        {
            return Results.Json(
                new { detail = $"Nenhum formatador disponível para '{language}'." },
                statusCode: StatusCodes.Status501NotImplemented);
        }

        try
        {
            using var doc = JsonDocument.Parse(request.Code);
            var formatted = JsonSerializer.Serialize(
                doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            return Results.Ok(new { code = formatted });
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { detail = $"JSON inválido: {ex.Message}" });
        }
    }
}

using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de geração de imagens, espelhando <c>/api/v1/images</c> do Open WebUI.</summary>
public static class ImageEndpoints
{
    /// <summary>Mapeia as rotas de imagens.</summary>
    public static RouteGroupBuilder MapImageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/images").RequireAuthorization();

        group.MapGet("/config", GetConfigAsync);
        group.MapPost("/config", UpdateConfigAsync);
        group.MapPost("/generations", GenerateAsync);

        return group;
    }

    private static async Task<IResult> GetConfigAsync(
        HttpContext http, ImageGenerationService images, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var config = await images.GetConfigAsync(ct);
        return Results.Ok(Masked(config));
    }

    private static async Task<IResult> UpdateConfigAsync(
        ImagesConfig request, HttpContext http, ImageGenerationService images, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        if (!string.Equals(request.Engine, "openai", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { detail = "Motor de imagens não suportado." });
        }

        var current = await images.GetConfigAsync(ct);
        var apiKey = request.ApiKey is "" or ImagesConfig.MaskedApiKey
            ? current.ApiKey
            : request.ApiKey;
        var updated = request with
        {
            Engine = "openai",
            ApiKey = apiKey,
            Model = string.IsNullOrWhiteSpace(request.Model) ? current.Model : request.Model,
            Size = string.IsNullOrWhiteSpace(request.Size) ? current.Size : request.Size,
            TimeoutSeconds = Math.Clamp(request.TimeoutSeconds, 5, 600),
        };
        await images.SetConfigAsync(updated, ct);
        return Results.Ok(Masked(updated));
    }

    private static async Task<IResult> GenerateAsync(
        ImageGenerationRequest request,
        HttpContext http,
        ImageGenerationService images,
        AppDbContext db,
        IWebHostEnvironment env,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return Results.BadRequest(new { detail = "Prompt obrigatório." });
        }

        var config = await images.GetConfigAsync(ct);
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }

        var uploadDir = Path.Combine(env.ContentRootPath, "data", "uploads", user.Id);
        try
        {
            var files = await images.GenerateAsync(
                request.Prompt, request.N ?? 1, request.Size, user.Id, uploadDir, ct);
            return Results.Ok(files
                .Select(f => new GeneratedImage($"/api/v1/files/{f.Id}/content"))
                .ToList());
        }
        catch (InvalidOperationException)
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
    }

    private static ImagesConfig Masked(ImagesConfig config) =>
        string.IsNullOrEmpty(config.ApiKey)
            ? config
            : config with { ApiKey = ImagesConfig.MaskedApiKey };
}

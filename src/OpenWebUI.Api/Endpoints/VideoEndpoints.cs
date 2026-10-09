using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.Services.Video;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Configuração de geração de vídeo (SPEC-20261007-chat-agent-parity
/// RF-019), espelhando <c>/api/v1/images/config</c>: admin lê/grava
/// <c>video.config</c>, lista motores e testa conectividade. A API key
/// volta mascarada; geração em si é feita pela tool
/// <c>builtin:generate_video</c> no chat.
/// </summary>
public static class VideoEndpoints
{
    /// <summary>Mapeia as rotas de configuração de vídeo.</summary>
    public static RouteGroupBuilder MapVideoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/videos").RequireAuthorization();

        group.MapGet("/config", GetConfigAsync);
        group.MapPost("/config", UpdateConfigAsync);
        group.MapGet("/config/engines", ListEnginesAsync);
        group.MapPost("/config/test", TestAsync);
        group.MapPost("/generations", GenerateAsync);

        return group;
    }

    private static async Task<IResult> GetConfigAsync(
        HttpContext http, VideoGenerationService videos, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var config = await videos.GetConfigAsync(ct);
        return Results.Ok(Masked(config));
    }

    private static async Task<IResult> UpdateConfigAsync(
        VideoConfig request, HttpContext http, VideoGenerationService videos,
        CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        if (!VideoEngineFactory.Engines.Contains(request.Engine.ToLowerInvariant()))
        {
            return Results.BadRequest(new { detail = "Motor de vídeo não suportado." });
        }

        var current = await videos.GetConfigAsync(ct);
        var apiKey = request.ApiKey is "" or ImagesConfig.MaskedApiKey
            ? current.ApiKey
            : request.ApiKey;
        var updated = request with
        {
            Engine = request.Engine.ToLowerInvariant(),
            ApiKey = apiKey,
            Model = string.IsNullOrWhiteSpace(request.Model) ? current.Model : request.Model,
            Size = string.IsNullOrWhiteSpace(request.Size) ? current.Size : request.Size,
            TimeoutSeconds = Math.Clamp(request.TimeoutSeconds, 5, 600),
        };
        await videos.SetConfigAsync(updated, ct);
        return Results.Ok(Masked(updated));
    }

    private static IResult ListEnginesAsync(HttpContext http) =>
        !http.User.IsInRole(UserRoles.Admin)
            ? Results.Forbid()
            : Results.Ok(VideoEngineFactory.Engines);

    private static async Task<IResult> TestAsync(
        HttpContext http, VideoGenerationService videos, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }
        try
        {
            var (ok, detail) = await videos.TestAsync(ct);
            return Results.Ok(new ImageTestResponse(ok, detail));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { detail = ex.Message });
        }
    }

    private static async Task<IResult> GenerateAsync(
        VideoGenerationRequest request, HttpContext http,
        VideoGenerationService videos, AppDbContext db, IWebHostEnvironment env,
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

        var config = await videos.GetConfigAsync(ct);
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }

        var uploadDir = Path.Join(DataPaths.Root(env.ContentRootPath), "uploads", user.Id);
        try
        {
            var files = await videos.GenerateAsync(
                request.Prompt, request.Seconds, request.Size, user.Id, uploadDir, ct);
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

    private static VideoConfig Masked(VideoConfig config) =>
        string.IsNullOrEmpty(config.ApiKey)
            ? config
            : config with { ApiKey = ImagesConfig.MaskedApiKey };
}

using System.Text.Json;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Configuração da integração n8n (SPEC-20261007-chat-agent-parity
/// RF-020): base URL + API key persistidas em kv
/// (<c>n8n.base_url</c>/<c>n8n.api_key</c>) por admin; a API key nunca
/// volta no GET — apenas <c>hasApiKey</c>. <c>GET /workflows</c> faz
/// proxy para a API pública do n8n para a UI/testes de conexão.
/// </summary>
public static class N8nEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia as rotas de configuração e proxy do n8n.</summary>
    public static RouteGroupBuilder MapN8nEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/n8n").RequireAuthorization();

        group.MapGet("/config", ConfigGetAsync);
        group.MapPut("/config", ConfigPutAsync);
        group.MapGet("/workflows", WorkflowsGetAsync);

        return group;
    }

    private static async Task<IResult> ConfigGetAsync(
        HttpContext http, N8nService n8n, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (user.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }

        return Results.Ok(new N8nConfigResponse(
            await n8n.IsConfiguredAsync(ct),
            await n8n.GetBaseUrlAsync(ct),
            await n8n.GetApiKeyAsync(ct) is not null));
    }

    private static async Task<IResult> ConfigPutAsync(
        HttpContext http, N8nService n8n, ConfigService config,
        AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (user.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }

        var request = await JsonSerializer.DeserializeAsync<N8nConfigUpdateRequest>(
            http.Request.Body, JsonOptions, ct) ?? new N8nConfigUpdateRequest(null, null);

        if (request.BaseUrl is { } baseUrl)
        {
            var trimmed = baseUrl.Trim();
            if (trimmed.Length > 0
                && (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https")))
            {
                return Results.BadRequest(new
                {
                    detail = "baseUrl deve ser uma URL http(s) absoluta.",
                });
            }

            await config.SetAsync<string?>(N8nService.BaseUrlKey,
                trimmed.Length == 0 ? null : trimmed, ct);
        }

        if (request.ApiKey is { } apiKey)
        {
            await config.SetAsync<string?>(N8nService.ApiKeyKey,
                apiKey.Trim().Length == 0 ? null : apiKey.Trim(), ct);
        }

        return Results.Ok(new N8nConfigResponse(
            await n8n.IsConfiguredAsync(ct),
            await n8n.GetBaseUrlAsync(ct),
            await n8n.GetApiKeyAsync(ct) is not null));
    }

    private static async Task<IResult> WorkflowsGetAsync(
        HttpContext http, N8nService n8n, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (user.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }

        try
        {
            var items = await n8n.ListWorkflowsAsync(ct);
            return Results.Ok(
                items.Select(w => new N8nWorkflowResponse(w.Id, w.Name, w.Active)));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { detail = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return Results.Json(
                new { detail = $"falha ao contatar o n8n: {ex.Message}" },
                statusCode: 502);
        }
    }

    private static Task<User?> CurrentUserAsync(
        HttpContext http, AppDbContext db, CancellationToken ct) =>
        AuthEndpoints.FindUserAsync(http, db, ct);
}

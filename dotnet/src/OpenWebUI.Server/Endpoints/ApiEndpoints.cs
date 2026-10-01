using System.Text;
using OpenWebUI.Server.Services;
using OpenWebUI.Shared.Contracts;

namespace OpenWebUI.Server.Endpoints;

/// <summary>Endpoints de modelos, completions e configurações de conexão.</summary>
public static class ApiEndpoints
{
    /// <summary>Mapeia as rotas de modelos e completions.</summary>
    public static void MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/version", () => Results.Ok(new VersionResponse("0.1.0-dotnet")));

        app.MapGet("/api/models", async (ProviderService providers, CancellationToken ct) =>
            Results.Ok(new ModelListResponse(await providers.ListModelsAsync(ct))))
            .RequireAuthorization();

        app.MapPost("/api/chat/completions", ChatCompletionsAsync).RequireAuthorization();

        var configs = app.MapGroup("/api/v1/configs").RequireAuthorization();
        configs.MapGet("/connections", GetConnectionsAsync);
        configs.MapPost("/connections", UpdateConnectionsAsync);
    }

    private static async Task ChatCompletionsAsync(
        ChatCompletionRequest request,
        HttpContext http,
        ProviderService providers,
        CancellationToken ct)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Connection = "keep-alive";

        await using var writer = new StreamWriter(http.Response.Body, Encoding.UTF8);
        try
        {
            await foreach (var line in providers.StreamCompletionAsync(request, ct))
            {
                await writer.WriteLineAsync(line);
                await writer.WriteLineAsync();
                await writer.FlushAsync();
            }
        }
        catch (InvalidOperationException ex)
        {
            var error = System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message });
            await writer.WriteLineAsync($"data: {error}");
            await writer.WriteLineAsync();
            await writer.FlushAsync();
            await writer.WriteLineAsync("data: [DONE]");
            await writer.WriteLineAsync();
            await writer.FlushAsync();
        }
        catch (HttpRequestException ex)
        {
            var error = System.Text.Json.JsonSerializer.Serialize(new { error = $"Falha ao contactar o provedor: {ex.Message}" });
            await writer.WriteLineAsync($"data: {error}");
            await writer.WriteLineAsync();
            await writer.FlushAsync();
            await writer.WriteLineAsync("data: [DONE]");
            await writer.WriteLineAsync();
            await writer.FlushAsync();
        }
    }

    private static async Task<IResult> GetConnectionsAsync(
        HttpContext http, ConfigService config, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }

        var connections = await config.GetConnectionsAsync(ct);
        return Results.Ok(new ConnectionsConfigResponse(
            connections.OllamaBaseUrls,
            connections.OpenAiBaseUrls,
            connections.OpenAiBaseUrls
                .Select((_, i) => !string.IsNullOrEmpty(connections.OpenAiApiKeys.ElementAtOrDefault(i)))
                .ToList()));
    }

    private static async Task<IResult> UpdateConnectionsAsync(
        ConnectionsConfig request,
        HttpContext http,
        ConfigService config,
        CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }

        // Preserva chaves existentes quando o campo vier vazio (UI não reenvia segredos).
        var current = await config.GetConnectionsAsync(ct);
        var currentUrls = current.OpenAiBaseUrls.ToList();
        var keys = request.OpenAiBaseUrls
            .Select((url, i) =>
            {
                var incoming = request.OpenAiApiKeys.ElementAtOrDefault(i);
                if (!string.IsNullOrEmpty(incoming))
                {
                    return incoming;
                }

                var previousIndex = currentUrls.IndexOf(url);
                return previousIndex >= 0 ? current.OpenAiApiKeys.ElementAtOrDefault(previousIndex) ?? string.Empty : string.Empty;
            })
            .ToList();

        var updated = request with { OpenAiApiKeys = keys };
        await config.SetAsync("connections", updated, ct);

        return Results.Ok(new ConnectionsConfigResponse(
            updated.OllamaBaseUrls,
            updated.OpenAiBaseUrls,
            updated.OpenAiBaseUrls
                .Select((_, i) => !string.IsNullOrEmpty(updated.OpenAiApiKeys.ElementAtOrDefault(i)))
                .ToList()));
    }

    private static bool IsAdmin(HttpContext http) =>
        http.User.IsInRole(UserRoles.Admin);
}

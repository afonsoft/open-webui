using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Api.Completions;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de modelos, completions, configuração pública e conexões.</summary>
public static class ApiEndpoints
{
    private const string BackendVersion = "0.11.4-dotnet";

    /// <summary>Mapeia as rotas de modelos, completions e configuração.</summary>
    public static void MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = true }));
        app.MapGet("/api/version", () => Results.Ok(new VersionResponse(BackendVersion)));
        app.MapGet("/api/version/updates", () =>
            Results.Ok(new VersionUpdateResponse(BackendVersion, BackendVersion)));
        app.MapGet("/api/changelog", () => Results.Ok(new { releases = Array.Empty<object>() }));

        app.MapGet("/api/config", GetAppConfigAsync);
        app.MapPost("/api/config", UpdateAppConfigAsync).RequireAuthorization();
        app.MapGet("/api/models", ListAllModelsAsync).RequireAuthorization();
        app.MapGet("/api/v1/models/base", ListAllModelsAsync).RequireAuthorization();
        app.MapPost("/api/chat/completions", ChatCompletionsAsync)
            .RequireAuthorization()
            .AddEndpointFilter(CompletionRateLimitFilterAsync);
        app.MapPost("/api/chat/completed", () => Results.Ok(new StatusResponse(true)))
            .RequireAuthorization();

        var configs = app.MapGroup("/api/v1/configs").RequireAuthorization();
        configs.MapGet("/connections", GetConnectionsAsync);
        configs.MapPost("/connections", UpdateConnectionsAsync);
        configs.MapGet("/capabilities", GetCapabilitiesAsync);
        configs.MapGet("/connections/models", ListConnectionModelsAsync);
        configs.MapGet("/connections/capabilities", GetConnectionCapabilitiesAsync);
        configs.MapGet("/export", ExportConfigAsync);
        configs.MapPost("/import", ImportConfigAsync);
    }

    /// <summary>
    /// Filtro de rate limiting para completions: aplica a janela por usuário
    /// apenas quando habilitado na config (default desligado — compat).
    /// </summary>
    private static async ValueTask<object?> CompletionRateLimitFilterAsync(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var config = http.RequestServices.GetRequiredService<ConfigService>();
        var limits = http.RequestServices.GetRequiredService<RateLimitService>();
        var rateConfig = await config.GetAsync(
            "ratelimit", RateLimitConfig.Default, http.RequestAborted);
        if (rateConfig.Enabled)
        {
            var userId = http.User
                .FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? http.Connection.RemoteIpAddress?.ToString()
                ?? "anon";
            var retryAfter = limits.TryAcquire(
                userId, rateConfig, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (retryAfter is { } retry)
            {
                http.Response.Headers.RetryAfter = retry.ToString();
                return Results.Json(
                    new { detail = "Limite de requisições excedido. Tente novamente em instantes." },
                    statusCode: 429);
            }
        }

        return await next(ctx);
    }

    private static async Task<IResult> GetAppConfigAsync(
        HttpContext http, ConfigService config, ImageGenerationService images,
        VideoGenerationService videos, AudioService audio, CancellationToken ct)
    {
        var adminConfig = await config.GetAdminConfigAsync(ct);
        var imagesConfig = await images.GetConfigAsync(ct);
        var videoConfig = await videos.GetConfigAsync(ct);
        var audioConfig = await audio.GetResolvedConfigAsync(ct);
        var modelsConfig = await ConfigEndpoints.GetModelsConfigInternalAsync(config, ct);
        var retrieval = await config.GetAsync("retrieval.config", RetrievalConfig.Default, ct);

        return Results.Ok(new AppConfigResponse(
            Status: true,
            Name: adminConfig.WebUiName,
            Version: BackendVersion,
            DefaultLocale: "pt-BR",
            Features: new AppFeatures(
                Auth: true,
                EnableSignup: adminConfig.EnableSignup,
                EnableLoginForm: adminConfig.EnableLoginForm,
                EnableApiKeys: adminConfig.EnableApiKeys,
                EnableMessageRating: adminConfig.EnableMessageRating,
                EnableFolders: adminConfig.EnableFolders,
                EnableMemories: adminConfig.EnableMemories,
                EnableNotes: true,
                EnableChannels: true,
                EnableWebSearch: retrieval.Engine is not "none",
                EnableImageGeneration: imagesConfig.Enabled,
                EnableCodeExecution: true,
                EnableCommunitySharing: true,
                EnableVideoGeneration: videoConfig.Enabled,
                EnableTextToSpeech: audioConfig.TtsEnabled,
                EnableSpeechToText: audioConfig.SttEnabled),
            DefaultModels: modelsConfig.DefaultModels,
            DefaultPromptSuggestions: modelsConfig.PromptSuggestions,
            OAuthProviders: OAuthProviderCatalog.ConfiguredProviders()));
    }

    /// <summary>Persiste as feature flags administráveis (somente admin).</summary>
    private static async Task<IResult> UpdateAppConfigAsync(
        AdminConfig request, HttpContext http, ConfigService config, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        await config.SetAsync("admin.config", request, ct);
        return Results.Ok(request);
    }

    private static async Task<IResult> ListAllModelsAsync(
        HttpContext http, ProviderService providers, AppDbContext db,
        PipelineClientService pipelines, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var models = await providers.ListModelsAsync(ct);

        // Inclui modelos personalizados ativos do workspace (do usuário + públicos).
        var custom = await db.ModelEntries.AsNoTracking()
            .Where(m => m.IsActive && (m.UserId == user!.Id || m.UserId == "public"))
            .ToListAsync(ct);

        foreach (var entry in custom)
        {
            if (await ModelEndpoints.HasModelAccessAsync(user!, entry, db, ct))
            {
                models.Add(new ModelInfo(entry.Id, entry.Name, "custom", "openwebui"));
            }
        }

        // Pipes de servidores de pipelines registrados (expostos como pipeline:{id}).
        var servers = await db.PipelineServers.AsNoTracking().ToListAsync(ct);
        foreach (var server in servers)
        {
            var pipes = await pipelines.ListPipesAsync(server, ct);
            if (pipes is not null)
            {
                models.AddRange(pipes.Select(
                    p => new ModelInfo($"pipeline:{p.Id}", p.Name, "pipeline", "openwebui")));
            }
        }

        return Results.Ok(new ModelListResponse(models));
    }

    private static async Task ChatCompletionsAsync(
        ChatCompletionRequest request,
        HttpContext http,
        AppDbContext db,
        ConfigService config,
        ProviderService providers,
        RagService rag,
        ToolExecutor toolExecutor,
        PipelineClientService pipelines,
        WebSearchService webSearch,
        WorkspaceRepoService repos,
        IWebHostEnvironment env,
        CancellationToken ct)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Connection = "keep-alive";
        // Desativa buffering do SSE em proxies (nginx) — senão os deltas
        // só chegam ao cliente quando a resposta fecha.
        http.Response.Headers["X-Accel-Buffering"] = "no";

        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            http.Response.StatusCode = 401;
            return;
        }

        await using var writer = new StreamWriter(http.Response.Body, Encoding.UTF8);
        Task EmitAsync(string line) { writer.WriteLine(line); writer.WriteLine(); return writer.FlushAsync(); }

        // Modelos pipeline:{id} são roteados ao servidor de pipelines externo.
        if (request.Model.StartsWith("pipeline:", StringComparison.Ordinal))
        {
            var pipeId = request.Model["pipeline:".Length..];
            var (server, body, valvesJson) = await ChatPipeline.PreparePipelineRouteAsync(
                request, db, pipelines, ct);
            if (server is null)
            {
                http.Response.StatusCode = 404;
                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(new { error = $"Pipe '{pipeId}' não encontrado." }));
                await writer.FlushAsync();
                return;
            }

            var proxied = await pipelines.RouteCompletionAsync(server, body, valvesJson, ct);
            if (proxied.Response is null)
            {
                http.Response.StatusCode = proxied.StatusCode;
                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(new { error = $"Falha no servidor de pipelines: {proxied.Error}" }));
                await writer.FlushAsync();
                return;
            }

            using var upstream = proxied.Response;
            http.Response.StatusCode = (int)upstream.StatusCode;
            await upstream.Content.CopyToAsync(http.Response.Body, ct);
            return;
        }

        // Modelos arena geram duas respostas anonimizadas de concorrentes sorteados.
        var arenaModel = request.Model.StartsWith("arena:", StringComparison.Ordinal)
            ? request.Model["arena:".Length..]
            : request.Model;
        if (await ChatPipeline.TryRunArenaAsync(
            request, arenaModel, user, db, config, rag, providers, webSearch, repos, EmitAsync, ct))
        {
            return;
        }

        var effective = await ChatPipeline.EnrichRequestAsync(
            request, user, db, config, rag, webSearch, repos, ct);

        // Filtros outlet do modelo custom: redação por regex nas linhas SSE.
        var outletRules = ModelFilterService.OutletRules(
            ModelFilterService.Parse(await db.ModelEntries.AsNoTracking()
                .Where(m => m.IsActive && (m.Id == request.Model || m.Name == request.Model)
                    && (m.UserId == user.Id || m.UserId == "public"))
                .Select(m => m.MetaJson).FirstOrDefaultAsync(ct)));

        try
        {
            var tools = request.ToolIds is { Count: > 0 }
                ? await toolExecutor.LoadEnabledAsync(user.Id, request.ToolIds, ct)
                : null;
            if (tools is { Count: > 0 })
            {
                effective = effective with
                {
                    Tools = tools
                        .Select(t => JsonSerializer.Deserialize<JsonElement>(t.SpecJson))
                        .ToList(),
                };
                var builtinContext = new BuiltinToolContext(
                    user.Id,
                    ChatId: null,
                    RunId: null,
                    // Com repo vinculado o workdir vira o checkout do repo (mesmo jail).
                    await repos.ResolveWorkdirAsync(user.Id, ct),
                    Path.Join(env.ContentRootPath, "data", "uploads", user.Id));
                var outcome = await ChatPipeline.RunToolLoopAsync(
                    effective, tools, toolExecutor, providers, ct,
                    builtinContext: builtinContext);
                if (outcome?.FinalContent is { } finished)
                {
                    foreach (var (regex, replacement) in outletRules)
                    {
                        finished = regex.Replace(finished, replacement);
                    }
                    var chunk = JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { index = 0, delta = new { content = finished } } },
                    });
                    await writer.WriteLineAsync($"data: {chunk}");
                    await writer.WriteLineAsync();
                    await writer.WriteLineAsync("data: [DONE]");
                    await writer.WriteLineAsync();
                    await writer.FlushAsync();
                    return;
                }
            }

            await foreach (var line in providers.StreamCompletionAsync(effective, ct))
            {
                await writer.WriteLineAsync(
                    ModelFilterService.ProcessSseLine(line, outletRules));
                await writer.WriteLineAsync();
                await writer.FlushAsync();
            }
        }
        catch (InvalidOperationException ex)
        {
            await WriteSseErrorAsync(writer, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            await WriteSseErrorAsync(writer, $"Falha ao contactar o provedor: {ex.Message}");
        }
    }

    private static async Task WriteSseErrorAsync(StreamWriter writer, string message)
    {
        var error = JsonSerializer.Serialize(new { error = message });
        await writer.WriteLineAsync($"data: {error}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("data: [DONE]");
        await writer.WriteLineAsync();
        await writer.FlushAsync();
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
                .ToList(),
            connections.OllamaNames,
            connections.OpenAiNames));
    }

    /// <summary>
    /// Modelos detectados por capacidade (admin). Lê o kv persistido pela
    /// detecção; se nunca rodou, faz uma classificação rápida do catálogo
    /// (GET /models, sem probes) — suficiente para os combos.
    /// </summary>
    private static async Task<IResult> GetCapabilitiesAsync(
        HttpContext http, ConfigService config,
        ProviderCapabilityService capabilities, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }

        var detected = await capabilities.GetAggregatedAsync(ct);
        if (detected is null)
        {
            var connections = await config.GetConnectionsAsync(ct);
            for (var i = 0; i < connections.OpenAiBaseUrls.Count && detected is null; i++)
            {
                try
                {
                    detected = await capabilities.DetectConnectionAsync("openai", i, ct);
                }
                catch (Exception)
                {
                    // Provider fora do ar: lista vazia, combos ficam em texto livre.
                }
            }
        }

        return detected is null
            ? Results.Ok(new DetectedCapabilities([], [], [], [], [], null, 0))
            : Results.Ok(detected);
    }

    /// <summary>
    /// Capacidades detectadas de UMA conexão (admin) — os combos de STT/TTS,
    /// imagem e vídeo filtram pelos modelos do provider selecionado, não pelo
    /// catálogo inteiro nem por outro provider. Faz a detecção rápida
    /// (/models + heurística) e atualiza o mapa persistido.
    /// </summary>
    private static async Task<IResult> GetConnectionCapabilitiesAsync(
        HttpContext http, ProviderCapabilityService capabilities,
        string type, int index, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        if (type is not ("openai" or "ollama"))
        {
            return Results.BadRequest(new { detail = "type deve ser openai|ollama." });
        }

        try
        {
            var detected = await capabilities.DetectConnectionAsync(type, index, ct);
            return Results.Ok(detected ?? new DetectedCapabilities([], [], [], [], [], null, 0));
        }
        catch (Exception)
        {
            // Provider fora do ar: devolve vazio — combos caem em texto livre.
            return Results.Ok(new DetectedCapabilities([], [], [], [], [], null, 0));
        }
    }

    /// <summary>Lista modelos de uma conexão cadastrada (admin) — usado pelos combos da UI.</summary>
    private static async Task<IResult> ListConnectionModelsAsync(
        HttpContext http, ProviderService providers,
        string type, int index, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }

        var models = await providers.ListModelsForConnectionAsync(type, index, ct);
        return Results.Ok(new ModelListResponse(models));
    }

    private static async Task<IResult> UpdateConnectionsAsync(
        ConnectionsConfig request,
        HttpContext http,
        ConfigService config,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
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

        // Detecção de capacidades em background: classifica /models e
        // probeia TTS/imagem/vídeo/embeddings, preenchendo configs ausentes.
        _ = Task.Run(async () =>
        {
            var log = loggerFactory.CreateLogger("OpenWebUI.ProviderCapability");
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ProviderCapabilityService>()
                    .AutoConfigureAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Auto-config de capacidades falhou.");
            }
        });

        return Results.Ok(new ConnectionsConfigResponse(
            updated.OllamaBaseUrls,
            updated.OpenAiBaseUrls,
            updated.OpenAiBaseUrls
                .Select((_, i) => !string.IsNullOrEmpty(updated.OpenAiApiKeys.ElementAtOrDefault(i)))
                .ToList(),
            updated.OllamaNames,
            updated.OpenAiNames));
    }

    private static async Task<IResult> ExportConfigAsync(
        HttpContext http, ConfigService config, AppDbContext db, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }

        var entries = await db.ConfigEntries.AsNoTracking()
            .ToDictionaryAsync(e => e.Key, e => e.ValueJson, ct);
        return Results.Ok(entries);
    }

    private static async Task<IResult> ImportConfigAsync(
        Dictionary<string, JsonElement> request,
        HttpContext http,
        ConfigService config,
        CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }

        foreach (var (key, value) in request)
        {
            await config.SetAsync(key, value, ct);
        }

        return Results.Ok(new StatusResponse(true));
    }

    private static bool IsAdmin(HttpContext http) =>
        http.User.IsInRole(UserRoles.Admin);
}

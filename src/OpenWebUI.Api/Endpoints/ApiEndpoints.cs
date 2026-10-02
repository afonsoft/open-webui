using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Application.Contracts;

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
        HttpContext http, ConfigService config, ImageGenerationService images, CancellationToken ct)
    {
        var adminConfig = await config.GetAdminConfigAsync(ct);
        var imagesConfig = await images.GetConfigAsync(ct);
        var modelsConfig = await ConfigEndpoints.GetModelsConfigInternalAsync(config, ct);

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
                EnableWebSearch: false,
                EnableImageGeneration: imagesConfig.Enabled,
                EnableCodeExecution: true,
                EnableCommunitySharing: true),
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
        CancellationToken ct)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Connection = "keep-alive";

        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            http.Response.StatusCode = 401;
            return;
        }

        await using var writer = new StreamWriter(http.Response.Body, Encoding.UTF8);

        // Modelos pipeline:{id} são roteados ao servidor de pipelines externo.
        if (request.Model.StartsWith("pipeline:", StringComparison.Ordinal))
        {
            await RoutePipelineAsync(request, http, db, pipelines, writer, ct);
            return;
        }

        // Modelos arena geram duas respostas anonimizadas de concorrentes sorteados.
        var arenaModel = request.Model.StartsWith("arena:", StringComparison.Ordinal)
            ? request.Model["arena:".Length..]
            : request.Model;
        if (await TryRunArenaAsync(
            request, arenaModel, user, db, config, rag, providers, writer, ct))
        {
            return;
        }

        var effective = await EnrichRequestAsync(request, user, db, config, rag, ct);

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
                var finished = await RunToolLoopAsync(effective, tools, toolExecutor, providers, ct);
                if (finished is not null)
                {
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
                await writer.WriteLineAsync(line);
                await writer.WriteLineAsync();
                await writer.FlushAsync();
            }
        }
        catch (InvalidOperationException ex)
        {
            var error = JsonSerializer.Serialize(new { error = ex.Message });
            await writer.WriteLineAsync($"data: {error}");
            await writer.WriteLineAsync();
            await writer.FlushAsync();
            await writer.WriteLineAsync("data: [DONE]");
            await writer.WriteLineAsync();
            await writer.FlushAsync();
        }
        catch (HttpRequestException ex)
        {
            var error = JsonSerializer.Serialize(new { error = $"Falha ao contactar o provedor: {ex.Message}" });
            await writer.WriteLineAsync($"data: {error}");
            await writer.WriteLineAsync();
            await writer.FlushAsync();
            await writer.WriteLineAsync("data: [DONE]");
            await writer.WriteLineAsync();
            await writer.FlushAsync();
        }
    }

    /// <summary>
    /// Executa uma batalha de arena quando o modelo pedido é do tipo arena:
    /// sorteia 2 concorrentes do MetaJson, completa ambos e emite um payload
    /// {"arena": {battle_id, responses:[{label, content}]}} no SSE.
    /// Retorna true quando tratou a requisição (mesmo em erro já serializado).
    /// </summary>
    private static async Task<bool> TryRunArenaAsync(
        ChatCompletionRequest request,
        string modelKey,
        User user,
        AppDbContext db,
        ConfigService config,
        RagService rag,
        ProviderService providers,
        StreamWriter writer,
        CancellationToken ct)
    {
        var arenaEntry = await db.ModelEntries.AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.IsActive && (m.Id == modelKey || m.Name == modelKey)
                    && (m.UserId == user.Id || m.UserId == "public"), ct);
        var arena = arenaEntry is null ? null : ModelEndpoints.ParseArenaMeta(arenaEntry.MetaJson);
        if (arena is null)
        {
            return request.Model.StartsWith("arena:", StringComparison.Ordinal);
        }
        if (!await ModelEndpoints.HasModelAccessAsync(user, arenaEntry!, db, ct))
        {
            await WriteArenaErrorAsync(writer, "Acesso negado ao modelo arena.");
            return true;
        }

        var competitors = arena.Value.ModelIds
            .OrderBy(_ => Random.Shared.Next())
            .Take(2)
            .ToList();
        if (competitors.Count < 2)
        {
            await WriteArenaErrorAsync(writer, "Modelo arena sem concorrentes suficientes.");
            return true;
        }

        var responses = new List<string>(2);
        try
        {
            foreach (var competitor in competitors)
            {
                var effective = await EnrichRequestAsync(
                    request with { Model = competitor, Stream = false },
                    user, db, config, rag, ct);
                responses.Add(await providers.CompleteAsync(effective, ct));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            await WriteArenaErrorAsync(writer, $"Falha ao gerar respostas da arena: {ex.Message}");
            return true;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var battle = new ArenaBattle
        {
            UserId = user.Id,
            ArenaModelId = arenaEntry!.Id,
            ModelA = competitors[0],
            ModelB = competitors[1],
            ResponseA = responses[0],
            ResponseB = responses[1],
            CreatedAt = now,
        };
        db.ArenaBattles.Add(battle);
        await db.SaveChangesAsync(ct);

        var payload = JsonSerializer.Serialize(new
        {
            arena = new
            {
                battle_id = battle.Id,
                responses = new[]
                {
                    new { label = "A", content = responses[0] },
                    new { label = "B", content = responses[1] },
                },
            },
        });
        await writer.WriteLineAsync($"data: {payload}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("data: [DONE]");
        await writer.WriteLineAsync();
        await writer.FlushAsync();
        return true;
    }

    /// <summary>
    /// Roteia uma completion `pipeline:{id}` ao servidor de pipelines que
    /// hospeda o pipe: 404 quando nenhum servidor conhece o id, 502 quando o
    /// servidor falha, e passthrough do corpo upstream (SSE) no sucesso.
    /// Valves das functions ativas são enviadas no corpo (`valves`).
    /// </summary>
    private static async Task RoutePipelineAsync(
        ChatCompletionRequest request,
        HttpContext http,
        AppDbContext db,
        PipelineClientService pipelines,
        StreamWriter writer,
        CancellationToken ct)
    {
        var pipeId = request.Model["pipeline:".Length..];
        var server = await pipelines.FindServerForPipeAsync(pipeId, ct);
        if (server is null)
        {
            http.Response.StatusCode = 404;
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(new { error = $"Pipe '{pipeId}' não encontrado." }));
            await writer.FlushAsync();
            return;
        }

        // Valves de functions ativas — enviadas ao servidor como {"valves": {id: {...}}}.
        var functions = await db.Functions.AsNoTracking()
            .Where(f => f.Active && f.ValvesJson != null)
            .Select(f => new { f.Id, f.ValvesJson }).ToListAsync(ct);
        string? valvesJson = null;
        if (functions.Count > 0)
        {
            var map = new Dictionary<string, JsonElement>();
            foreach (var f in functions)
            {
                try
                {
                    map[f.Id] = JsonSerializer.Deserialize<JsonElement>(f.ValvesJson!);
                }
                catch (JsonException)
                {
                }
            }

            valvesJson = JsonSerializer.Serialize(map);
        }

        var body = JsonSerializer.Serialize(new
        {
            model = pipeId,
            messages = request.Messages,
            stream = request.Stream,
        });
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
        await writer.FlushAsync();
    }

    private static Task WriteArenaErrorAsync(StreamWriter writer, string message) =>
        WriteSseErrorAsync(writer, message);

    private static async Task WriteSseErrorAsync(StreamWriter writer, string message)
    {
        var error = JsonSerializer.Serialize(new { error = message });
        await writer.WriteLineAsync($"data: {error}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("data: [DONE]");
        await writer.WriteLineAsync();
        await writer.FlushAsync();
    }

    /// <summary>
    /// Loop de tool calling: chama o modelo com tools até resposta final
    /// (sem tool_calls) ou teto de 5 iterações. Retorna o conteúdo final
    /// para ser emitido como SSE, ou null para seguir o stream normal.
    /// </summary>
    private static async Task<string?> RunToolLoopAsync(
        ChatCompletionRequest effective,
        IReadOnlyList<Tool> tools,
        ToolExecutor toolExecutor,
        ProviderService providers,
        CancellationToken ct)
    {
        const int maxRounds = 5;
        var messages = effective.Messages.ToList();

        for (var round = 0; round < maxRounds; round++)
        {
            var step = await providers.CompleteWithToolsAsync(
                effective with { Messages = messages }, ct);
            if (step.ToolCalls.Count == 0)
            {
                return step.Content;
            }

            messages.Add(new ChatCompletionMessage(
                "assistant", step.Content, ToolCallsJson: step.ToolCallsJson));
            foreach (var call in step.ToolCalls)
            {
                var output = await toolExecutor.ExecuteAsync(
                    tools, call.Name, call.ArgumentsJson, ct);
                messages.Add(new ChatCompletionMessage(
                    "tool", output, ToolCallId: call.Id));
            }
        }

        // Teto de iterações atingido: resposta final sem tools.
        var final = await providers.CompleteAsync(
            effective with { Messages = messages, Tools = null }, ct);
        return final;
    }

    /// <summary>Aplica modelo personalizado, contexto de arquivos e memórias à requisição.</summary>
    private static async Task<ChatCompletionRequest> EnrichRequestAsync(
        ChatCompletionRequest request,
        User user,
        AppDbContext db,
        ConfigService config,
        RagService rag,
        CancellationToken ct)
    {
        var model = request.Model;
        var messages = request.Messages.ToList();
        var parameters = request.Params?.ToDictionary(kv => kv.Key, kv => kv.Value);
        var systemParts = new List<string>();

        // 1. Modelo personalizado do workspace → redireciona para o modelo base e aplica config.
        var customModel = await db.ModelEntries.AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.IsActive && (m.Id == model || m.Name == model)
                    && (m.UserId == user.Id || m.UserId == "public"), ct);
        if (customModel is not null
            && !await ModelEndpoints.HasModelAccessAsync(user, customModel, db, ct))
        {
            customModel = null;
        }
        if (customModel is not null)
        {
            if (!string.IsNullOrWhiteSpace(customModel.BaseModelId))
            {
                model = customModel.BaseModelId;
            }
            if (!string.IsNullOrWhiteSpace(customModel.SystemPrompt))
            {
                systemParts.Add(customModel.SystemPrompt);
            }

            if (!string.IsNullOrWhiteSpace(customModel.ParamsJson))
            {
                try
                {
                    var customParams = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        customModel.ParamsJson);
                    if (customParams is not null)
                    {
                        parameters ??= [];
                        foreach (var (key, value) in customParams)
                        {
                            parameters.TryAdd(key, value);
                        }
                    }
                }
                catch (JsonException)
                {
                }
            }
        }

        // 1.5. Skills anexadas ao modelo custom (MetaJson.skill_ids) → system prompt.
        if (customModel?.MetaJson is not null)
        {
            try
            {
                using var meta = JsonDocument.Parse(customModel.MetaJson);
                if (meta.RootElement.TryGetProperty("skill_ids", out var skillIds)
                    && skillIds.ValueKind == JsonValueKind.Array)
                {
                    var ids = skillIds.EnumerateArray()
                        .Select(e => e.GetString()).Where(s => s is not null).ToList();
                    var contents = await db.Skills.AsNoTracking()
                        .Where(s => ids.Contains(s.Id) && s.IsActive)
                        .Select(s => s.Content).ToListAsync(ct);
                    systemParts.AddRange(contents.Where(c => !string.IsNullOrWhiteSpace(c)));
                }
            }
            catch (JsonException)
            {
            }
        }

        // 2. Contexto de arquivos/referências: anexos + #arquivo/#coleção.
        //    Com embeddings disponíveis injeta os top-K chunks por similaridade;
        //    sem provider cai no fallback de texto integral atual.
        var lastUserText = messages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
        var referenced = await ResolveReferenceFileIdsAsync(lastUserText, user.Id, db, ct);
        var scopedFileIds = (request.FileIds ?? [])
            .Concat(referenced).Distinct().ToList();
        if (scopedFileIds.Count > 0)
        {
            var fileContext = lastUserText.Length > 0
                ? await rag.RetrieveAsync(user.Id, lastUserText, scopedFileIds, ct)
                : null;
            fileContext ??= await FileEndpoints.BuildFileContextAsync(
                scopedFileIds, user.Id, db, ct);
            if (!string.IsNullOrEmpty(fileContext))
            {
                systemParts.Add(fileContext);
            }
        }

        // 3. Memórias persistentes do usuário.
        var adminConfig = await config.GetAdminConfigAsync(ct);
        if (adminConfig.EnableMemories)
        {
            var memories = await db.Memories.AsNoTracking()
                .Where(m => m.UserId == user.Id)
                .Select(m => m.Content)
                .ToListAsync(ct);
            if (memories.Count > 0)
            {
                systemParts.Add(
                    "Memórias do usuário:\n" + string.Join("\n", memories.Select(m => $"- {m}")));
            }
        }

        // 4. Mescla partes de sistema numa única mensagem inicial.
        if (systemParts.Count > 0)
        {
            var merged = string.Join("\n\n", systemParts);
            var existing = messages.FindIndex(m => m.Role == "system");
            if (existing >= 0)
            {
                messages[existing] = messages[existing] with { Content = $"{merged}\n\n{messages[existing].Content}" };
            }
            else
            {
                messages.Insert(0, new ChatCompletionMessage("system", merged));
            }
        }

        return request with
        {
            Model = model,
            Messages = messages,
            Params = parameters,
            FileIds = null,
        };
    }

    /// <summary>Resolve referências #nome (arquivo ou coleção) para ids de arquivo.</summary>
    private static async Task<List<string>> ResolveReferenceFileIdsAsync(
        string text, string userId, AppDbContext db, CancellationToken ct)
    {
        var fileIds = new List<string>();
        if (!text.Contains('#'))
        {
            return fileIds;
        }

        var tokens = System.Text.RegularExpressions.Regex
            .Matches(text, @"#([\w.\-]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        if (tokens.Count == 0)
        {
            return fileIds;
        }

        // #coleção → todos os arquivos vinculados.
        var collections = await db.KnowledgeCollections.AsNoTracking()
            .Where(k => k.UserId == userId && tokens.Contains(k.Name))
            .Select(k => k.Id)
            .ToListAsync(ct);
        if (collections.Count > 0)
        {
            fileIds.AddRange(await db.KnowledgeFiles.AsNoTracking()
                .Where(f => collections.Contains(f.CollectionId))
                .Select(f => f.FileId)
                .ToListAsync(ct));
        }

        // #arquivo → arquivo do usuário com esse nome.
        fileIds.AddRange(await db.Files.AsNoTracking()
            .Where(f => f.UserId == userId && tokens.Contains(f.Filename))
            .Select(f => f.Id)
            .ToListAsync(ct));

        return fileIds;
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

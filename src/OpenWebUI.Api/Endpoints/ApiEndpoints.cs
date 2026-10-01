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
        app.MapGet("/api/models", ListAllModelsAsync).RequireAuthorization();
        app.MapGet("/api/v1/models/base", ListAllModelsAsync).RequireAuthorization();
        app.MapPost("/api/chat/completions", ChatCompletionsAsync).RequireAuthorization();
        app.MapPost("/api/chat/completed", () => Results.Ok(new StatusResponse(true)))
            .RequireAuthorization();

        var configs = app.MapGroup("/api/v1/configs").RequireAuthorization();
        configs.MapGet("/connections", GetConnectionsAsync);
        configs.MapPost("/connections", UpdateConnectionsAsync);
        configs.MapGet("/export", ExportConfigAsync);
        configs.MapPost("/import", ImportConfigAsync);
    }

    private static async Task<IResult> GetAppConfigAsync(
        HttpContext http, ConfigService config, CancellationToken ct)
    {
        var adminConfig = await config.GetAdminConfigAsync(ct);

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
                EnableImageGeneration: false,
                EnableCodeExecution: false,
                EnableCommunitySharing: true),
            DefaultPromptSuggestions: [],
            OAuthProviders: OAuthProviderCatalog.ConfiguredProviders()));
    }

    private static async Task<IResult> ListAllModelsAsync(
        HttpContext http, ProviderService providers, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var models = await providers.ListModelsAsync(ct);

        // Inclui modelos personalizados ativos do workspace (do usuário + públicos).
        var custom = await db.ModelEntries.AsNoTracking()
            .Where(m => m.IsActive && (m.UserId == user!.Id || m.UserId == "public"))
            .ToListAsync(ct);

        foreach (var entry in custom)
        {
            models.Add(new ModelInfo(entry.Id, entry.Name, "custom", "openwebui"));
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

        var effective = await EnrichRequestAsync(request, user, db, config, rag, ct);

        await using var writer = new StreamWriter(http.Response.Body, Encoding.UTF8);
        try
        {
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
        if (customModel is not null)
        {
            model = customModel.BaseModelId;
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

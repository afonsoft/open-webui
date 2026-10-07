using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Memory;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Api.Endpoints;
using OpenWebUI.Api.Runs;
using OpenWebUI.Api.Notifications;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.Services.Image;
using OpenWebUI.Infrastructure.Terminal;
using OpenWebUI.Application.Contracts;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=data/openwebui.db";

builder.Services.AddDbContext<AppDbContext>(options =>
    ConfigureDatabase(options, connectionString));

builder.Services.AddScoped<ConfigService>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<ProviderService>();
builder.Services.AddScoped<AutomationService>();
builder.Services.AddHostedService<AutomationScheduler>();
builder.Services.AddScoped<OAuthService>();
builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<EmbeddingService>();
builder.Services.AddScoped<RagService>();
builder.Services.AddScoped<ToolExecutor>();
builder.Services.AddScoped<PythonToolExecutor>();
builder.Services.AddSingleton<ImageEngineFactory>();
builder.Services.AddScoped<ImageGenerationService>();
builder.Services.AddScoped<AudioService>();
builder.Services.AddScoped<WebLoaderService>();
builder.Services.AddScoped<WebSearchService>();
builder.Services.AddScoped<AccessControlService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<ProviderProxyService>();
builder.Services.AddScoped<TerminalProxyService>();
builder.Services.AddSingleton<LocalTerminalSpawner>();
builder.Services.AddSingleton<TerminalSessionManager>();
builder.Services.AddScoped<ScimService>();
builder.Services.AddScoped<SamlService>();
builder.Services.AddScoped<PipelineClientService>();
builder.Services.AddScoped<McpClientService>();
builder.Services.AddSingleton<RateLimitService>();

// Runs de chat desacopladas (SPEC-20261007-chat-detached-runs).
builder.Services.AddSingleton<ChatRunBroadcaster>();
builder.Services.AddSingleton<ChatRunApprovals>();
builder.Services.AddSingleton<ChatRunPauses>();
builder.Services.AddSingleton<ChatRunDispatcher>();
builder.Services.AddSingleton<IChatRunDispatcher>(
    sp => sp.GetRequiredService<ChatRunDispatcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatRunDispatcher>());
builder.Services.AddScoped<ChatRunExecutor>();

// Notificações de run (SPEC-20261007-chat-notifications): SignalR para abas
// conectadas + Web Push quando nenhuma aba está conectada.
builder.Services.AddScoped<VapidKeyService>();
builder.Services.AddScoped<IWebPushSender, WebPushSender>();
builder.Services.AddScoped<IChatRunNotifier, SignalRChatRunNotifier>();
builder.Services.AddScoped<IChatRunNotifier, WebPushChatRunNotifier>();
builder.Services.AddHttpClient("webpush");

// Tools built-in do chat (SPEC-20261007-chat-agent-tools): registro
// resolve ids "builtin:*"; desligar via config BuiltinTools:Disabled (csv).
builder.Services.AddScoped<IBuiltinChatTool, GenerateImageBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, CodeInterpreterBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, ShellExecBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, JobListBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, JobOutputBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, JobKillBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, FetchUrlBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, WebSearchBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, FileListBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, FileReadBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, FileGrepBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, FileGlobBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, FileWriteBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, FileEditBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, TodoWriteBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, DelegateTaskBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, BrowserScreenshotBuiltinTool>();
builder.Services.AddSingleton<BrowserScreenshotService>();
builder.Services.AddScoped<BuiltinToolRegistry>();
builder.Services.AddSingleton<ChatJobService>();
builder.Services.AddHttpClient(nameof(FetchUrlBuiltinTool));

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddOpenApi();

// Inicializa o banco e obtém o segredo JWT antes de configurar a autenticação.
string jwtSecret;
using (var bootstrap = new AppDbContext(CreateDbOptions(connectionString)))
{
    if (connectionString.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
    {
        var path = connectionString["Data Source=".Length..].Split(';')[0].Trim();
        if (!string.IsNullOrEmpty(path) && path != ":memory:")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
    }

    DatabaseMigrator.MigrateAsync(bootstrap).GetAwaiter().GetResult();
    SeedConnectionsFromEnv(bootstrap);
    SeedWhisperUrlFromEnv(bootstrap);
    SeedAdminUserFromEnv(bootstrap);
    var entry = bootstrap.ConfigEntries.Find("webui.jwt.secret");
    if (entry is null)
    {
        var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        bootstrap.ConfigEntries.Add(new ConfigEntry
        {
            Key = "webui.jwt.secret",
            ValueJson = System.Text.Json.JsonSerializer.Serialize(secret),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        bootstrap.SaveChanges();
        jwtSecret = secret;
    }
    else
    {
        jwtSecret = System.Text.Json.JsonSerializer.Deserialize<string>(entry.ValueJson)!;
    }
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = JwtTokenService.BuildValidationParameters(jwtSecret);
        options.Events = new JwtBearerEvents
        {
            // SignalR envia o JWT via query string no handshake do WebSocket.
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    (context.HttpContext.Request.Path.StartsWithSegments("/ws") ||
                     context.HttpContext.Request.Path.StartsWithSegments("/api/v1/terminals")))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddSignalR();

// JSON das APIs (listas de chats, configs, i18n) é o payload que mais se repete;
// os assets WASM já são servidos comprimidos pelo MapStaticAssets.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

var app = builder.Build();

app.UseResponseCompression();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseWebAssemblyDebugging();
}

// Atrás de reverse proxy (nginx/Cloudflare/preview): honra X-Forwarded-*
// para que Scheme/Host reflitam a URL real — sem isso o redirect_uri do
// OAuth sai como http://interno e o callback quebra (mesmo fix do agent-harness).
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost,
});

// Serve os static web assets com fingerprinting e resolve os placeholders
// #[.{fingerprint}] do index.html (UseStaticFiles não faz essa substituição).
app.MapStaticAssets();

// Documento/rotas da SPA e a cadeia mutável de boot (index.html, boot.js e os
// loaders não-fingerprinted) sempre revalidam — caso contrário uma cópia antiga
// em cache continua apontando para fingerprints antigos e o deploy nunca chega
// ao browser (previne também o loop de reload do PWA conhecido no upstream).
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var isDocument =
            context.Request.Method == "GET" &&
            !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/ws", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/framework-assets/", StringComparison.OrdinalIgnoreCase) &&
            (path == "/" || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("/service-worker.js", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("/js/boot.js", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("/manifest.webmanifest", StringComparison.OrdinalIgnoreCase) ||
             path is "/_framework/blazor.webassembly.js"
                 or "/_framework/dotnet.js"
                 or "/_framework/dotnet.boot.js" ||
             !path.Contains('.'));
        if (isDocument)
        {
            context.Response.Headers.CacheControl = "no-cache";
        }

        return Task.CompletedTask;
    });

    await next();
});

app.UseAuthentication();

// Chaves de API (Bearer sk-...) autenticam como o usuário dono da chave.
app.Use(async (context, next) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    if (header.StartsWith("Bearer sk-", StringComparison.Ordinal))
    {
        var key = header["Bearer ".Length..].Trim();
        var hash = AuthEndpoints.HashApiKey(key);
        var memoryCache = context.RequestServices.GetRequiredService<IMemoryCache>();
        // Flag lida fora do cache (ConfigService já cacheia) para que desligar
        // API keys passe a valer imediatamente para chaves já resolvidas.
        using (var flagScope = context.RequestServices.CreateScope())
        {
            var flagConfig = flagScope.ServiceProvider.GetRequiredService<ConfigService>();
            var flagAdmin = await flagConfig.GetAdminConfigAsync();
            if (!flagAdmin.EnableApiKeys)
            {
                await next();
                return;
            }
        }
        // Cache de 2min: evita escopo DI + 2 queries ao SQLite por request.
        // Revogação/rotação evicta explicitamente em AuthEndpoints; o TTL curto
        // cobre mudanças de role (admin rebaixa usuário) fora desse caminho.
        var identity = await memoryCache.GetOrCreateAsync(
            ApiKeyAuthCache.CacheKey(hash),
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = ApiKeyAuthCache.Ttl;
                using var scope = context.RequestServices.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var apiKey = await db.ApiKeys.AsNoTracking()
                    .FirstOrDefaultAsync(k => k.KeyHash == hash);
                var user = apiKey is null
                    ? null
                    : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == apiKey.UserId);
                if (user is null || user.Role == UserRoles.Pending)
                {
                    return null;
                }
                return new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, user.Id),
                    new Claim(ClaimTypes.Name, user.Name),
                    new Claim(ClaimTypes.Email, user.Email),
                    new Claim(ClaimTypes.Role, user.Role),
                ], "ApiKey");
            });
        if (identity is not null)
        {
            context.User = new ClaimsPrincipal(identity);
        }
    }

    await next();
});

app.UseAuthorization();
app.UseWebSockets();

app.MapAuthEndpoints();
app.MapNotificationEndpoints();
app.MapPassthroughEndpoints();
app.MapTerminalEndpoints();
app.MapTerminalPtyEndpoints();
app.MapBrowserToolEndpoints();
app.MapTerminalWebSocket();
app.MapScimEndpoints();
app.MapSamlEndpoints();
app.MapPluginEndpoints();
app.MapChatEndpoints();
app.MapUserEndpoints();
app.MapWorkspaceEndpoints();
app.MapUtilsEndpoints();
app.MapFileEndpoints();
app.MapModelEndpoints();
app.MapEvaluationEndpoints();
app.MapAnalyticsEndpoints();
app.MapTaskEndpoints();
app.MapApiEndpoints();
app.MapGroupEndpoints();
app.MapOAuthEndpoints();
app.MapKnowledgeEndpoints();
app.MapToolEndpoints();
app.MapChatJobEndpoints();
app.MapMcpEndpoints();
app.MapChannelEndpoints();
app.MapImageEndpoints();
app.MapAutomationEndpoints();
app.MapConfigEndpoints();
app.MapAudioEndpoints();
app.MapRetrievalEndpoints();
app.MapCalendarEndpoints();
app.MapFrameworkAssetsEndpoints();
app.MapHub<OpenWebUI.Api.Hubs.ChatHub>("/ws");

app.MapFallbackToFile("index.html");

app.Run();

static void ConfigureDatabase(DbContextOptionsBuilder options, string connectionString) =>
    options.UseSqlite(connectionString);

/// <summary>
/// Semeia as conexões com provedores a partir de variáveis de ambiente (mesmos
/// nomes do Open WebUI original) apenas na primeira execução — depois disso a
/// configuração é gerenciada pela UI admin.
/// </summary>
static void SeedConnectionsFromEnv(AppDbContext db)
{
    if (db.ConfigEntries.Find("connections") is not null)
    {
        return;
    }

    var ollama = SplitEnvUrls(
        Environment.GetEnvironmentVariable("OLLAMA_BASE_URLS")
        ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL"));
    var openAi = SplitEnvUrls(
        Environment.GetEnvironmentVariable("OPENAI_API_BASE_URLS")
        ?? Environment.GetEnvironmentVariable("OPENAI_API_BASE_URL"));
    var openAiKeys = SplitEnvUrls(
        Environment.GetEnvironmentVariable("OPENAI_API_KEYS")
        ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

    if (ollama.Count == 0 && openAi.Count == 0)
    {
        return;
    }

    db.ConfigEntries.Add(new ConfigEntry
    {
        Key = "connections",
        ValueJson = System.Text.Json.JsonSerializer.Serialize(
            new ConnectionsConfig(ollama, openAi, openAiKeys)),
        UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    });
    db.SaveChanges();
}

/// <summary>WHISPER_URL configura a engine STT whisper (faster-whisper-server) no primeiro boot.</summary>
static void SeedWhisperUrlFromEnv(AppDbContext db)
{
    var whisperUrl = Environment.GetEnvironmentVariable("WHISPER_URL");
    if (string.IsNullOrWhiteSpace(whisperUrl)
        || db.ConfigEntries.Find("audio.config") is not null)
    {
        return;
    }

    var config = AudioConfig.Default with { SttEngine = "whisper", SttBaseUrl = whisperUrl };
    db.ConfigEntries.Add(new ConfigEntry
    {
        Key = "audio.config",
        ValueJson = System.Text.Json.JsonSerializer.Serialize(
            config, new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web)),
        UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    });
    db.SaveChanges();
}

/// <summary>
/// Cria o usuário admin inicial a partir de <c>ADMIN_EMAIL</c> +
/// <c>ADMIN_PASSWORD</c> (opcional <c>ADMIN_NAME</c>) somente quando a base
/// está vazia — equivalente ao primeiro signup virar admin, mas via env.
/// </summary>
static void SeedAdminUserFromEnv(AppDbContext db)
{
    var email = Environment.GetEnvironmentVariable("ADMIN_EMAIL")?.Trim().ToLowerInvariant();
    var password = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)
        || db.Users.Any())
    {
        return;
    }

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var name = Environment.GetEnvironmentVariable("ADMIN_NAME");
    var user = new User
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Admin" : name.Trim(),
        Email = email,
        Role = UserRoles.Admin,
        PermissionsJson = "{}",
        CreatedAt = now,
        UpdatedAt = now,
    };
    user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
    db.Users.Add(user);
    db.SaveChanges();
}

static List<string> SplitEnvUrls(string? value) =>
    value?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [];

static DbContextOptions<AppDbContext> CreateDbOptions(string connectionString)
{
    var builder = new DbContextOptionsBuilder<AppDbContext>();
    ConfigureDatabase(builder, connectionString);
    return builder.Options;
}

/// <summary>Ponto de entrada para testes de integração com WebApplicationFactory.</summary>
public partial class Program;

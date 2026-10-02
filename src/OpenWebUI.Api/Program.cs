using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Api.Endpoints;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.Services.Image;
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
builder.Services.AddSingleton<ImageEngineFactory>();
builder.Services.AddScoped<ImageGenerationService>();
builder.Services.AddScoped<AccessControlService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<ProviderProxyService>();
builder.Services.AddScoped<TerminalProxyService>();
builder.Services.AddScoped<ScimService>();
builder.Services.AddScoped<SamlService>();
builder.Services.AddScoped<PipelineClientService>();
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

var app = builder.Build();

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

// Documento/rotas da SPA sempre revalidam (previne o loop de reload do PWA
// conhecido no upstream); assets fingerprinted já saem immutable via MapStaticAssets.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var isDocument =
            context.Request.Method == "GET" &&
            !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/ws", StringComparison.OrdinalIgnoreCase) &&
            (path == "/" || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("/service-worker.js", StringComparison.OrdinalIgnoreCase) ||
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
        using var scope = context.RequestServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var configService = scope.ServiceProvider.GetRequiredService<ConfigService>();
        var adminConfig = await configService.GetAdminConfigAsync();
        if (adminConfig.EnableApiKeys)
        {
            var apiKey = await db.ApiKeys.AsNoTracking()
                .FirstOrDefaultAsync(k => k.KeyHash == hash);
            if (apiKey is not null)
            {
                var user = await db.Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == apiKey.UserId);
                if (user is not null && user.Role != UserRoles.Pending)
                {
                    var identity = new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, user.Id),
                        new Claim(ClaimTypes.Name, user.Name),
                        new Claim(ClaimTypes.Email, user.Email),
                        new Claim(ClaimTypes.Role, user.Role),
                    ], "ApiKey");
                    context.User = new ClaimsPrincipal(identity);
                }
            }
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
app.MapScimEndpoints();
app.MapSamlEndpoints();
app.MapPluginEndpoints();
app.MapChatEndpoints();
app.MapUserEndpoints();
app.MapWorkspaceEndpoints();
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
app.MapChannelEndpoints();
app.MapImageEndpoints();
app.MapAutomationEndpoints();
app.MapConfigEndpoints();
app.MapCalendarEndpoints();
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

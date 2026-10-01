using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Server.Data;
using OpenWebUI.Server.Endpoints;
using OpenWebUI.Server.Services;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=data/openwebui.db";

builder.Services.AddDbContext<AppDbContext>(options =>
    ConfigureDatabase(options, connectionString));

builder.Services.AddScoped<ConfigService>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<ProviderService>();
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

    bootstrap.Database.EnsureCreated();
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
    });
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseWebAssemblyDebugging();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapChatEndpoints();
app.MapApiEndpoints();

app.MapFallbackToFile("index.html");

app.Run();

static void ConfigureDatabase(DbContextOptionsBuilder options, string connectionString) =>
    options.UseSqlite(connectionString);

static DbContextOptions<AppDbContext> CreateDbOptions(string connectionString)
{
    var builder = new DbContextOptionsBuilder<AppDbContext>();
    ConfigureDatabase(builder, connectionString);
    return builder.Options;
}

/// <summary>Ponto de entrada para testes de integração com WebApplicationFactory.</summary>
public partial class Program;

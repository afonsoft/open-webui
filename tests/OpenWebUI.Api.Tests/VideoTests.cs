using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.Services.Video;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura do RF-019 (<c>builtin:generate_video</c>): tool sem
/// config/args, motores OpenAI-compatible e ComfyUI contra handler fake,
/// factory e endpoints admin de <c>/api/v1/videos/config</c>.
/// </summary>
[TestFixture]
public class VideoTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _uploadDir = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-video-{Guid.NewGuid():N}.db");
        _uploadDir = Path.Join(Path.GetTempPath(), $"openwebui-video-up-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@video.local");
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
        if (Directory.Exists(_uploadDir))
        {
            Directory.Delete(_uploadDir, recursive: true);
        }
    }

    // ---------------- builtin:generate_video ----------------

    [Test]
    public async Task GenerateVideo_ArgsObrigatoriosEDesabilitada()
    {
        var tool = new GenerateVideoBuiltinTool(NewVideoService());
        var semPrompt = await tool.ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(semPrompt.Text, Does.Contain("prompt").And.Contain("obrigatório"));

        var r = await tool.ExecuteAsync(
            Args("{\"prompt\":\"um gato correndo\",\"seconds\":4}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("falh").Or.Contain("desabilitada"));
    }

    [Test]
    public async Task GenerateVideo_MotorOpenAi_PersisteMp4ComVideoPath()
    {
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/v1/videos" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { id = "vid-1" }),
                },
                "/v1/videos/vid-1" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { status = "completed" }),
                },
                "/v1/videos/vid-1/content" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3, 4]),
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        var db = NewIsolatedDb();
        using var mc3 = new MemoryCache(new MemoryCacheOptions());
        var config = new ConfigService(db, mc3);
        var service = new VideoGenerationService(
            new VideoEngineFactory(new StubFactory(handler)), config, db);
        await service.SetConfigAsync(VideoConfig.Default with
        {
            Enabled = true,
            BaseUrl = "http://video.test/v1",
        }, default);

        var tool = new GenerateVideoBuiltinTool(service);
        var r = await tool.ExecuteAsync(
            Args("{\"prompt\":\"ondas do mar\",\"seconds\":4}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("Vídeo gerado"));
        Assert.That(r.Text, Does.Contain("/api/v1/files/"));
        Assert.That(JsonSerializer.Serialize(r.Result), Does.Contain("videoPath"));

        var saved = db.Files.FirstOrDefault(f => f.ContentType == "video/mp4");
        Assert.That(saved, Is.Not.Null);
        Assert.That(File.Exists(saved!.StoragePath), Is.True);
    }

    [Test]
    public async Task ComfyUiVideo_ColetaSaidaDeVideoEFiltraImagens()
    {
        var handler = new FakeHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/prompt")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { prompt_id = "p-1" }),
                };
            }
            if (uri.AbsolutePath == "/history/p-1")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new Dictionary<string, object>
                    {
                        ["p-1"] = new
                        {
                            outputs = new
                            {
                                node9 = new
                                {
                                    gifs = new[]
                                    {
                                        new { filename = "clip.mp4", subfolder = "", type = "output" },
                                        new { filename = "frame.png", subfolder = "", type = "output" },
                                    },
                                },
                            },
                        },
                    }),
                };
            }
            if (uri.AbsolutePath == "/view")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([9, 9, 9]),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var db = NewIsolatedDb();
        using var mc2 = new MemoryCache(new MemoryCacheOptions());
        var config = new ConfigService(db, mc2);
        var service = new VideoGenerationService(
            new VideoEngineFactory(new StubFactory(handler)), config, db);
        await service.SetConfigAsync(VideoConfig.Default with
        {
            Enabled = true,
            Engine = "comfyui",
            BaseUrl = "http://comfy.test",
            EngineParams = "{\"workflow\":\"{\\\"9\\\":{\\\"inputs\\\":{\\\"prompt\\\":\\\"{prompt}\\\",\\\"seed\\\":{seed}}}}\"}",
        }, default);

        var files = await service.GenerateAsync(
            "um cometa", 4, null, "u1", _uploadDir, default);
        Assert.That(files, Has.Count.EqualTo(1)); // frame.png filtrado
        Assert.That(files[0].Filename, Does.EndWith(".mp4"));
        Assert.That(files[0].ContentType, Is.EqualTo("video/mp4"));
    }

    [Test]
    public void VideoFactory_MotorDesconhecido_Lanca()
    {
        var factory = new VideoEngineFactory(new StubFactory());
        Assert.That(factory.Resolve("openai").Name, Is.EqualTo("openai"));
        Assert.That(factory.Resolve("comfyui").Name, Is.EqualTo("comfyui"));
        Assert.Throws<InvalidOperationException>(() => factory.Resolve("midjourney"));
    }

    // ---------------- /api/v1/videos/config (admin) ----------------

    [Test]
    public async Task VideosConfig_UsuarioComum_Proibido()
    {
        var user = await SignUpAsync("Vuser", "vuser@video.local");
        UseToken(user.Token);
        var get = await _client.GetAsync("/api/v1/videos/config");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task VideosConfig_Admin_LeAtualizaETestaSemVazarKey()
    {
        var signin = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("admin@video.local", "senha123"));
        var admin = (await signin.Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(admin.Token);

        var config = await _client.GetFromJsonAsync<VideoConfig>("/api/v1/videos/config");
        Assert.That(config!.Enabled, Is.False);

        var updated = await _client.PostAsJsonAsync("/api/v1/videos/config",
            VideoConfig.Default with
            {
                Enabled = true,
                BaseUrl = "http://video.invalid/v1",
                ApiKey = "segredo-video",
            });
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        var raw = await _client.GetStringAsync("/api/v1/videos/config");
        Assert.That(raw, Does.Not.Contain("segredo-video"));
        Assert.That(raw, Does.Contain("********"));

        var engines = await _client.GetFromJsonAsync<List<string>>("/api/v1/videos/config/engines");
        Assert.That(engines, Does.Contain("openai").And.Contain("comfyui"));

        var bad = await _client.PostAsJsonAsync("/api/v1/videos/config",
            VideoConfig.Default with { Engine = "midjourney" });
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Motor desconhecido no teste de conectividade? não — config válida;
        // host inválido → (ok=false) ou HTTP error, mas nunca exception crua.
        var test = await _client.PostAsync("/api/v1/videos/config/test", null);
        Assert.That((int)test.StatusCode, Is.LessThan(500).Or.EqualTo(502));
    }

    // ---------------- Helpers ----------------

    private VideoGenerationService NewVideoService()
    {
        var db = NewIsolatedDb();
        using var mc1 = new MemoryCache(new MemoryCacheOptions());
        return new VideoGenerationService(
            new VideoEngineFactory(new StubFactory()),
            new ConfigService(db, mc1), db);
    }

    private AppDbContext NewIsolatedDb()
    {
        var path = Path.Join(Path.GetTempPath(),
            $"openwebui-video-svc-{Guid.NewGuid():N}.db");
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        DatabaseMigrator.MigrateAsync(db).GetAwaiter().GetResult();
        return db;
    }

    private BuiltinToolContext Ctx() => new(
        "u-test", null, null,
        _uploadDir,
        _uploadDir);

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, "senha123"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            handler is null ? new HttpClient() : new HttpClient(handler);
    }
}

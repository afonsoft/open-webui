using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.Services.Image;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes das tools <c>generate_image</c> e <c>browser_screenshot</c>
/// (SPEC-20261008-tests-coverage-gate RF-002): caminhos de validação,
/// geração com engine HTTP fake, persistência de FileEntry e mensagens
/// de erro claras para o modelo.
/// </summary>
public class ImageAndBrowserToolsTests
{
    private string _workspace = null!;
    private string _uploadDir = null!;
    private AppDbContext _db = null!;
    private HybridCache _cache = null!;
    private ConfigService _config = null!;
    private RoutingHandler _handler = null!;
    private ImageGenerationService _images = null!;
    private GenerateImageBuiltinTool _imageTool = null!;

    [SetUp]
    public void SetUp()
    {
        _workspace = Path.Join(Path.GetTempPath(), $"owui-img-{Guid.NewGuid():N}");
        _uploadDir = Path.Join(_workspace, "uploads");
        Directory.CreateDirectory(_uploadDir);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Join(_workspace, "t.db")}").Options);
        DatabaseMigrator.MigrateAsync(_db).GetAwaiter().GetResult();
        _cache = TestCache.Create();
        _config = new ConfigService(_db, _cache);
        _handler = new RoutingHandler();
        var factory = new ImageEngineFactory(new StubFactory(_handler));
        _images = new ImageGenerationService(factory, _config, _db);
        _imageTool = new GenerateImageBuiltinTool(_images);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
                _handler.Dispose();
        try { Directory.Delete(_workspace, true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private BuiltinToolContext Ctx() => new("u1", "c1", "r1", _workspace, _uploadDir);
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private Task EnableImages() => _config.SetAsync("images.config",
        ImagesConfig.Default with
        {
            Enabled = true,
            BaseUrl = "http://img.test",
            ApiKey = "sk-img",
        }, default);

    // ---------------- generate_image ----------------

    [Test]
    public async Task Img_PromptObrigatorio()
    {
        var r = await _imageTool.ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("prompt"));
    }

    [Test]
    public async Task Img_Desabilitada_ErroClaro()
    {
        var r = await _imageTool.ExecuteAsync(
            Args("{\"prompt\":\"gato\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("falhou").Or.Contain("desabilitada"));
    }

    [Test]
    public async Task Img_Sucesso_UmaImagem()
    {
        await EnableImages();
        var png = Convert.ToBase64String(new byte[] { 137, 80, 78, 71 });
        _handler.Respond(HttpStatusCode.OK,
            $"{{\"data\":[{{\"b64_json\":\"{png}\"}}]}}");

        var r = await _imageTool.ExecuteAsync(
            Args("{\"prompt\":\"gato\",\"size\":\"512x512\"}"), Ctx(), default);

        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("Imagem gerada"));
            Assert.That(r.Text, Does.Contain("/api/v1/files/"));
            Assert.That(_handler.LastRequest!.RequestUri!.ToString(),
                Is.EqualTo("http://img.test/images/generations"));
            Assert.That(_handler.LastRequest.Headers.Authorization!.Scheme,
                Is.EqualTo("Bearer"));
        });
        var file = _db.Files.Single(f => f.UserId == "u1");
        Assert.Multiple(() =>
        {
            Assert.That(file.ContentType, Does.Contain("image"));
            Assert.That(File.Exists(file.StoragePath), Is.True);
        });
    }

    [Test]
    public async Task Img_Sucesso_NImagens_Plural()
    {
        await EnableImages();
        var png = Convert.ToBase64String(new byte[] { 1 });
        _handler.Respond(HttpStatusCode.OK,
            $"{{\"data\":[{{\"b64_json\":\"{png}\"}},{{\"b64_json\":\"{png}\"}}]}}");

        var r = await _imageTool.ExecuteAsync(
            Args("{\"prompt\":\"dois gatos\",\"n\":2}"), Ctx(), default);

        Assert.That(r.Text, Does.Contain("2 imagens geradas"));
        Assert.That(_db.Files.Count(f => f.UserId == "u1"), Is.EqualTo(2));
    }

    [Test]
    public async Task Img_ErroDoEngine_ViraMensagem()
    {
        await EnableImages();
        _handler.Respond(HttpStatusCode.InternalServerError,
            "{\"error\":\"boom\"}", "text/plain");

        var r = await _imageTool.ExecuteAsync(
            Args("{\"prompt\":\"x\"}"), Ctx(), default);

        Assert.That(r.Text, Does.Contain("falhou"));
    }

    [Test]
    public async Task Img_DataVazio_SemImagens()
    {
        await EnableImages();
        _handler.Respond(HttpStatusCode.OK,
            "{\"data\":[]}");

        var r = await _imageTool.ExecuteAsync(
            Args("{\"prompt\":\"x\"}"), Ctx(), default);

        Assert.That(r.Text,
            Does.Contain("não retornou imagens").Or.Contain("sem imagens").Or.Contain("falhou"));
    }

    // ---------------- browser_screenshot ----------------

    private BrowserScreenshotBuiltinTool BrowserTool(IConfiguration? cfg = null)
    {
        var c = cfg ?? new ConfigurationBuilder().Build();
        return new BrowserScreenshotBuiltinTool(
            new BrowserScreenshotService(c,
                NullLogger<BrowserScreenshotService>.Instance),
            _config, c, _db);
    }

    [Test]
    public async Task Browser_Desabilitada_PorPadrao()
    {
        var tool = BrowserTool();
        var r = await tool.ExecuteAsync(
            Args("{\"url\":\"https://example.com\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("desabilitada"));
    }

    [Test]
    public async Task Browser_HabilitadaUrlInvalida_Erro()
    {
        var tool = BrowserTool(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BrowserTools:Enabled"] = "true",
            }).Build());
        var r = await tool.ExecuteAsync(
            Args("{\"url\":\"ftp://x\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("URL inválida"));
    }

    [Test]
    public async Task Browser_HabilitadaHostPrivado_Bloqueado()
    {
        var tool = BrowserTool(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BrowserTools:Enabled"] = "true",
            }).Build());
        var r = await tool.ExecuteAsync(
            Args("{\"url\":\"http://169.254.169.254/latest/meta-data\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("privado").Or.Contain("bloqueado"));
    }

    [Test]
    public async Task Browser_SemBinario_ScreenshotFalhou()
    {
        // BrowserTools:Path apontado para um executável que existe mas sai
        // com código != 0 sem produzir imagem: resolve como "binário" e o
        // launch "falha" → "Screenshot falhou" de forma determinística, com
        // ou sem Chrome na máquina.
        var tool = BrowserTool(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BrowserTools:Enabled"] = "true",
                ["BrowserTools:Path"] = "/bin/false",
            }).Build());
        var r = await tool.ExecuteAsync(
            Args("{\"url\":\"http://127.0.0.1:9/\",\"width\":800,\"height\":600}"),
            Ctx(), default);
        Assert.That(r.Text, Does.Contain("Screenshot falhou"));
    }

    // ---------------- helpers ----------------

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private byte[] _body = "{}"u8.ToArray();
        private string? _mediaType = "application/json";

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public void Respond(HttpStatusCode status, string body,
            string mediaType = "application/json")
        {
            _status = status;
            _body = Encoding.UTF8.GetBytes(body);
            _mediaType = mediaType;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var fresh = new StringContent(Encoding.UTF8.GetString(_body),
                Encoding.UTF8, _mediaType ?? "application/json");
            return new HttpResponseMessage(_status) { Content = fresh };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}

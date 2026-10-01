using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da geração de imagens: config admin (chave mascarada), feature flag,
/// geração persistindo arquivo e autorização dos endpoints /api/v1/images.
/// </summary>
[TestFixture]
public class ImageEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _dbPath = null!;
    private string _mockBaseUrl = null!;
    private string _adminToken = null!;

    private const string PngB64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _mockBaseUrl = StartMock();
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-images-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@images.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mockCts.Cancel();
        _mock.Stop();
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    /// <summary>Sobe um provedor de imagens mockado numa porta livre.</summary>
    private string StartMock()
    {
        var random = new Random();
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port = random.Next(40000, 60000);
            _mock = new HttpListener();
            _mock.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _mock.Start();
                break;
            }
            catch (HttpListenerException)
            {
                _mock.Close();
            }
        }
        if (!_mock.IsListening)
        {
            throw new InvalidOperationException("Nenhuma porta livre para o mock.");
        }
        _mockCts = new CancellationTokenSource();
        _ = Task.Run(() => MockLoopAsync(_mockCts.Token));
        return $"http://localhost:{port}";
    }

    private async Task MockLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _mock.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }

            var json = ctx.Request.Url!.AbsolutePath == "/images/generations"
                ? $"{{\"data\":[{{\"b64_json\":\"{PngB64}\"}}]}}"
                : "{}";
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode =
                ctx.Request.Url!.AbsolutePath == "/images/generations" ? 200 : 404;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email, string password)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, password));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private ImagesConfig MockConfig(bool enabled = true) => new(
        enabled, "openai", _mockBaseUrl, "sk-test", "gpt-image-1", "1024x1024", 30);

    [Test, Order(1)]
    public async Task Config_SomenteAdmin_GravaEMascaraChave()
    {
        var user = await SignUpAsync("User", "user@images.local", "senha123");
        UseToken(user.Token);
        var forbidden = await _client.PostAsJsonAsync("/api/v1/images/config", MockConfig());
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_adminToken);
        var saved = await _client.PostAsJsonAsync("/api/v1/images/config", MockConfig());
        Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var config = await _client.GetFromJsonAsync<ImagesConfig>("/api/v1/images/config");
        Assert.That(config, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(config!.ApiKey, Is.EqualTo(ImagesConfig.MaskedApiKey));
            Assert.That(config.Model, Is.EqualTo("gpt-image-1"));
        });
    }

    [Test, Order(2)]
    public async Task Config_ChaveMascarada_PreservaChaveReal()
    {
        UseToken(_adminToken);
        var masked = await _client.PostAsJsonAsync("/api/v1/images/config",
            MockConfig() with { ApiKey = ImagesConfig.MaskedApiKey });
        Assert.That(masked.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // A geração continua funcionando (chave real "sk-test" preservada).
        var gen = await _client.PostAsJsonAsync("/api/v1/images/generations",
            new ImageGenerationRequest("um gato", 1, null));
        Assert.That(gen.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test, Order(3)]
    public async Task Generations_FeatureOff_Retorna501()
    {
        UseToken(_adminToken);
        await _client.PostAsJsonAsync("/api/v1/images/config", MockConfig(enabled: false));

        var response = await _client.PostAsJsonAsync("/api/v1/images/generations",
            new ImageGenerationRequest("um gato", 1, null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotImplemented));

        var flags = await _client.GetFromJsonAsync<AppConfigResponse>("/api/config");
        Assert.That(flags!.Features.EnableImageGeneration, Is.False);

        await _client.PostAsJsonAsync("/api/v1/images/config", MockConfig());
    }

    [Test, Order(4)]
    public async Task Generations_SemAuth_Retorna401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var response = await _client.PostAsJsonAsync("/api/v1/images/generations",
            new ImageGenerationRequest("um gato", 1, null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test, Order(5)]
    public async Task Generations_GeraESalvaArquivoComoOwner()
    {
        var user = await SignUpAsync("Maker", "maker@images.local", "senha123");
        UseToken(user.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/images/generations",
            new ImageGenerationRequest("um gato", 1, null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var images = await response.Content.ReadFromJsonAsync<List<GeneratedImage>>();
        Assert.That(images, Has.Count.EqualTo(1));

        var url = images![0].Url;
        Assert.That(url, Does.StartWith("/api/v1/files/"));

        var content = await _client.GetAsync(url);
        Assert.That(content.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var bytes = await content.Content.ReadAsByteArrayAsync();
        Assert.That(bytes, Has.Length.GreaterThan(0));
        Assert.That(content.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/png"));

        var files = await _client.GetFromJsonAsync<List<FileResponse>>("/api/v1/files/");
        Assert.That(files!.Any(f => f.Filename.StartsWith("generated-")), Is.True);
    }
}

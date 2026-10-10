using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes dos motores de imagem (a1111, gemini, comfyui, openai edits) com
/// backend mockado via <see cref="HttpListener"/>.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class ImageEnginesTests
{
    private static readonly string PngB64 = Convert.ToBase64String(
        [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3]);

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _dbPath = null!;
    private string _mockBaseUrl = string.Empty;
    private AuthResponse _admin = null!;
    private AuthResponse _user = null!;

    /// <summary>Sobe a app e o backend de imagens multi-engine mockado.</summary>
    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _mockBaseUrl = StartMock();
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-imgengines-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _admin = await SignUpAsync("Admin", "admin@imgengines.local", "senha123");
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        _client.DefaultRequestHeaders.Authorization = null;
        _user = await SignUpAsync("User", "user@imgengines.local", "senha123");
    }

    /// <summary>Encerra os recursos.</summary>
    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mockCts.Cancel();
        _mock.Close();
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

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

            var path = ctx.Request.Url!.AbsolutePath;
            var (status, json) = path switch
            {
                "/sdapi/v1/txt2img" => (200, $"{{\"images\":[\"{PngB64}\"]}}"),
                "/sdapi/v1/img2img" => (200, $"{{\"images\":[\"{PngB64}\"]}}"),
                "/sdapi/v1/options" => (200, "{}"),
                "/prompt" => (200, "{\"prompt_id\":\"pid-1\"}"),
                "/history/pid-1" => (200,
                    "{\"pid-1\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"out.png\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}"),
                "/view" => (-1, ""),
                "/images/edits" => (200, $"{{\"data\":[{{\"b64_json\":\"{PngB64}\"}}]}}"),
                "/models" => (200, "{\"data\":[]}"),
                var p when p.StartsWith("/v1beta/models/", StringComparison.Ordinal) &&
                    p.EndsWith(":predict", StringComparison.Ordinal)
                    => (200, $"{{\"predictions\":[{{\"bytesBase64Encoded\":\"{PngB64}\"}}]}}"),
                var p when p.StartsWith("/v1beta/models/", StringComparison.Ordinal)
                    => (200, "{}"),
                _ => (404, "{}"),
            };

            if (status == -1)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "image/png";
                await ctx.Response.OutputStream.WriteAsync(Convert.FromBase64String(PngB64));
                ctx.Response.Close();
                continue;
            }

            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json));
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

    private async Task SetEngineAsync(string engine, string engineParams = "{}")
    {
        UseToken(_admin.Token);
        var response = await _client.PostAsJsonAsync("/api/v1/images/config",
            new ImagesConfig(true, engine, _mockBaseUrl, "sk-test", "modelo", "1024x1024", 30, engineParams));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    private async Task<int> GenerateAsync()
    {
        UseToken(_user.Token);
        var response = await _client.PostAsJsonAsync("/api/v1/images/generations",
            new ImageGenerationRequest("um gato", 1, null));
        return (int)response.StatusCode;
    }

    [Test]
    public async Task Motor_Desconhecido_Rejeitado() // RF-001
    {
        UseToken(_admin.Token);
        var response = await _client.PostAsJsonAsync("/api/v1/images/config",
            new ImagesConfig(true, "dalle-magico", _mockBaseUrl, "", "m", "1024x1024", 30, "{}"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Engines_Listadas_SomenteAdmin() // RF-004
    {
        UseToken(_admin.Token);
        var engines = await _client.GetFromJsonAsync<List<string>>("/api/v1/images/config/engines");
        Assert.That(engines, Is.EquivalentTo(new[] { "openai", "a1111", "gemini", "comfyui" }));

        UseToken(_user.Token);
        var forbidden = await _client.GetAsync("/api/v1/images/config/engines");
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task A1111_Txt2Img_GeraImagem() // RF-002
    {
        await SetEngineAsync("a1111", "{\"steps\":5}");
        Assert.That(await GenerateAsync(), Is.EqualTo(200));

        await _client.PostAsJsonAsync("/api/v1/images/config/test", new { });
        UseToken(_admin.Token);
        var result = await _client.PostAsJsonAsync("/api/v1/images/config/test", new { });
        var test = await result.Content.ReadFromJsonAsync<ImageTestResponse>();
        Assert.That(test!.Ok, Is.True);
    }

    [Test]
    public async Task Gemini_Predict_GeraImagem() // RF-002
    {
        await SetEngineAsync("gemini");
        Assert.That(await GenerateAsync(), Is.EqualTo(200));
    }

    [Test]
    public async Task ComfyUI_Workflow_GeraImagem() // RF-002
    {
        var workflow = "{\"3\":{\"inputs\":{\"text\":\"{prompt}\",\"seed\":{seed},\"steps\":{steps}},\"class_type\":\"KSampler\"}}";
        await SetEngineAsync("comfyui", $"{{\"workflow\":{System.Text.Json.JsonSerializer.Serialize(workflow)}}}");
        Assert.That(await GenerateAsync(), Is.EqualTo(200));
    }

    [Test]
    public async Task ComfyUI_SemWorkflow_FalhaCom501() // RF-002
    {
        await SetEngineAsync("comfyui", "{}");
        Assert.That(await GenerateAsync(), Is.EqualTo(501));
    }

    [Test]
    public async Task Edit_EngineSemSuporte_501() // RF-003
    {
        await SetEngineAsync("gemini");
        UseToken(_user.Token);
        var response = await _client.PostAsJsonAsync("/api/v1/images/edit",
            new ImageEditRequest("qualquer", "mais azul", null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotImplemented));
    }

    [Test]
    public async Task Edit_A1111_Img2Img_Ok() // RF-003
    {
        // gera uma imagem para ter um arquivo de origem
        await SetEngineAsync("a1111");
        UseToken(_user.Token);
        var generated = await _client.PostAsJsonAsync("/api/v1/images/generations",
            new ImageGenerationRequest("base", 1, null));
        var images = await generated.Content.ReadFromJsonAsync<List<GeneratedImage>>();
        var fileId = images![0].Url.Split('/').SkipLast(1).Last();

        var response = await _client.PostAsJsonAsync("/api/v1/images/edit",
            new ImageEditRequest(fileId, "mais vermelho", null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var edited = await response.Content.ReadFromJsonAsync<GeneratedImage>();
        Assert.That(edited!.Url, Does.Contain("/api/v1/files/"));
    }
}

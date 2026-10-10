using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes de filtros declarativos por modelo (SPEC model-filters):
/// system_inject e regex_redact no inlet, params cap, outlet na saída SSE
/// e validação do MetaJson no salvamento.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class ModelFiltersTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;
    private string _lastBody = "";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var mockBase = StartMock();
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-filters-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("OLLAMA_BASE_URL", mockBase);
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        Environment.SetEnvironmentVariable("OLLAMA_BASE_URL", null);

        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest("Admin", "admin@filters.local", "senha123"));
        _admin = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(_admin.Token);
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
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private string StartMock()
    {
        var random = new Random();
        int port = 0;
        for (var i = 0; i < 20; i++)
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
            string json;
            if (path == "/api/chat")
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                _lastBody = await reader.ReadToEndAsync(ct);
                // Resposta contém um "cpf" para testar a redação no outlet.
                json = "{\"message\":{\"content\":\"cpf 12345678901 ok\"}}";
                ctx.Response.StatusCode = 200;
            }
            else if (path == "/api/tags")
            {
                json = "{\"models\":[{\"name\":\"m1\"}]}";
                ctx.Response.StatusCode = 200;
            }
            else
            {
                json = "{}";
                ctx.Response.StatusCode = 404;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
        }
    }

    private async Task<string> CreateModelAsync(string metaJson, string name)
    {
        var created = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest(name, "m1", null, null, null, metaJson));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return name;
    }

    private async Task<string> CompleteAsync(string model, string userText)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/completions")
        {
            Content = JsonContent.Create(new ChatCompletionRequest(
                model, [new ChatCompletionMessage("user", userText)])),
        };
        var response = await _client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await response.Content.ReadAsStringAsync();
    }

    [Test]
    public async Task SystemInject_IncluiTextoNoPromptEnviado()
    {
        await CreateModelAsync(
            """{"filters":[{"type":"system_inject","config":{"text":"INJETADO-XYZ","position":"append"}}]}""",
            "filtro-inject");
        await CompleteAsync("filtro-inject", "oi");

        Assert.That(_lastBody, Does.Contain("INJETADO-XYZ"));
    }

    [Test]
    public async Task RegexRedact_RemovePadrao_DoConteudoEnviado()
    {
        await CreateModelAsync(
            """{"filters":[{"type":"regex_redact","config":{"pattern":"\\d{11}","replacement":"[cpf]","stage":"inlet"}}]}""",
            "filtro-redact");
        await CompleteAsync("filtro-redact", "meu cpf 12345678901");

        Assert.Multiple(() =>
        {
            Assert.That(_lastBody, Does.Contain("[cpf]"));
            Assert.That(_lastBody, Does.Not.Contain("12345678901"));
        });
    }

    [Test]
    public async Task RegexRedact_Outlet_RedigeSaidaSse()
    {
        await CreateModelAsync(
            """{"filters":[{"type":"regex_redact","config":{"pattern":"\\d{11}","replacement":"[cpf]","stage":"outlet"}}]}""",
            "filtro-outlet");
        var sse = await CompleteAsync("filtro-outlet", "diga o cpf");

        Assert.Multiple(() =>
        {
            Assert.That(sse, Does.Contain("[cpf]"));
            Assert.That(sse, Does.Not.Contain("12345678901"));
        });
    }

    [Test]
    public async Task MaxTokensCap_LimitaParametroEnviado()
    {
        await CreateModelAsync(
            """{"filters":[{"type":"max_tokens_cap","config":{"max_tokens":64}}]}""",
            "filtro-cap");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/completions")
        {
            Content = JsonContent.Create(new ChatCompletionRequest(
                "filtro-cap", [new ChatCompletionMessage("user", "oi")],
                Params: new Dictionary<string, object> { ["max_tokens"] = 500 })),
        };
        var response = await _client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await response.Content.ReadAsStringAsync();

        Assert.That(_lastBody, Does.Contain("\"num_predict\":64"));
    }

    [Test]
    public async Task ParamsOverride_SobrescreveParametroEnviado()
    {
        await CreateModelAsync(
            """{"filters":[{"type":"params_override","config":{"temperature":0.11}}]}""",
            "filtro-params");
        await CompleteAsync("filtro-params", "oi");

        Assert.That(_lastBody, Does.Contain("\"temperature\":0.11"));
    }

    [Test]
    public async Task Save_FilterInvalido_Retorna400()
    {
        var bad = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest("ruim", "m1", null, null, null,
                """{"filters":[{"type":"regex_redact","config":{"pattern":"(("}}]}"""));
        var unknown = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest("ruim2", "m1", null, null, null,
                """{"filters":[{"type":"python_code","config":{}}]}"""));
        var malformed = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest("ruim3", "m1", null, null, null,
                """{"filters":"nao-array"}"""));

        Assert.Multiple(() =>
        {
            Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    [Test]
    public async Task ModeloSemFiltros_FluxoInalterado()
    {
        await CreateModelAsync("""{"note":"sem filtros"}""", "filtro-nenhum");
        var sse = await CompleteAsync("filtro-nenhum", "cpf 12345678901");

        Assert.Multiple(() =>
        {
            Assert.That(_lastBody, Does.Contain("12345678901"));
            Assert.That(sse, Does.Contain("12345678901"));
        });
    }
}

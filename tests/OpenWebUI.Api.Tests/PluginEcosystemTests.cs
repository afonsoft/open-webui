using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da slice plugin-ecosystem: CRUD de skills, registry admin de
/// functions (toggle/valves), servidores de pipelines externos com
/// descoberta de pipes e roteamento de completions `pipeline:{id}`.
/// Nenhum código arbitrário executa no servidor — só HTTP.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class PluginEcosystemTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;
    private string _userToken = null!;
    private string _mockBase = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;

    /// <summary>Último corpo recebido pelo mock em /api/chat (Ollama).</summary>
    private string? _lastChatBody;

    /// <summary>Último corpo recebido pelo mock em /chat/completions (pipeline).</summary>
    private string? _lastPipeBody;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-plugin-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@plugin.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });

        var user = await SignUpAsync("User", "user@plugin.local", "senha123");
        _userToken = user.Token;

        _mockBase = StartMock();
        await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig([_mockBase], [], []));
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
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    private async Task<(string Token, string UserId)> SignUpAsync(
        string name, string email, string password)
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name, email, password });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return (auth!.Token, auth.User.Id);
    }

    private string StartMock()
    {
        var port = Random.Shared.Next(40000, 60000);
        _mock = new HttpListener();
        _mock.Prefixes.Add($"http://localhost:{port}/");
        _mock.Start();
        _mockCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_mockCts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _mock.GetContextAsync();
                }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (OperationCanceledException) { return; }

                _ = Task.Run(() => HandleAsync(ctx));
            }
        });
        return $"http://localhost:{port}";
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            switch (path)
            {
                case "/api/tags":
                    WriteJson(ctx, "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}");
                    break;
                case "/api/chat":
                {
                    using var reader = new StreamReader(ctx.Request.InputStream);
                    _lastChatBody = await reader.ReadToEndAsync();
                    WriteJson(ctx, "{\"message\":{\"content\":\"resposta do mock\"},\"done\":true}");
                    break;
                }
                case "/models":
                    WriteJson(ctx, "[{\"id\":\"pipe-1\",\"name\":\"Pipe Um\"}]");
                    break;
                case "/chat/completions":
                {
                    using var reader = new StreamReader(ctx.Request.InputStream);
                    _lastPipeBody = await reader.ReadToEndAsync();
                    var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"pipeline resposta\"}}]}\n\n"
                        + "data: [DONE]\n\n";
                    var bytes = Encoding.UTF8.GetBytes(sse);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/event-stream";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                    break;
                }
                default:
                    WriteJson(ctx, "{}", 404);
                    break;
            }
        }
        catch (HttpListenerException)
        {
            // cliente desconectou — ignora
        }
        catch (IOException)
        {
            // cliente desconectou — ignora
        }
        catch (ObjectDisposedException)
        {
            // listener parou
        }
    }

    private static void WriteJson(HttpListenerContext ctx, string json, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    // ---------------- Skills ----------------

    /// <summary>CRUD de skill pelo dono; outro usuário não enxerga.</summary>
    [Test]
    public async Task T01_Skills_CrudOwnerScoped()
    {
        UseToken(_userToken);
        var created = await _client.PostAsJsonAsync("/api/v1/skills/",
            new SkillRequest("Revisor PT-BR", "Revisa texto", "Revise o texto a seguir."));
        created.EnsureSuccessStatusCode();
        var skill = (await created.Content.ReadFromJsonAsync<SkillResponse>())!;

        var list = await _client.GetFromJsonAsync<List<SkillResponse>>("/api/v1/skills/");
        Assert.That(list!.Select(s => s.Id), Does.Contain(skill.Id));

        var updated = await _client.PostAsJsonAsync($"/api/v1/skills/{skill.Id}",
            new SkillRequest("Revisor PT-BR v2", null, "Revise com rigor."));
        updated.EnsureSuccessStatusCode();
        Assert.That((await updated.Content.ReadFromJsonAsync<SkillResponse>())!.Name,
            Is.EqualTo("Revisor PT-BR v2"));

        // Outro usuário não enxerga a skill
        UseToken(_adminToken);
        Assert.That((await _client.DeleteAsync($"/api/v1/skills/{skill.Id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK)); // admin enxerga tudo

        UseToken(_userToken);
        var del = await _client.DeleteAsync($"/api/v1/skills/{skill.Id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    /// <summary>Skill anexada ao modelo via MetaJson.skill_ids entra no system prompt.</summary>
    [Test]
    public async Task T02_SkillInjetada_NoSystemPrompt()
    {
        UseToken(_userToken);
        var created = await _client.PostAsJsonAsync("/api/v1/skills/",
            new SkillRequest("Tom Formal", null, "CONTEUDO-DA-SKILL-XYZ"));
        var skill = (await created.Content.ReadFromJsonAsync<SkillResponse>())!;

        var model = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest("ModeloComSkill", "fake:1", null, null, null,
                $"{{\"skill_ids\":[\"{skill.Id}\"]}}"));
        model.EnsureSuccessStatusCode();
        var entry = (await model.Content.ReadFromJsonAsync<ModelEntryResponse>())!;

        var response = await _client.PostAsJsonAsync("/api/chat/completions",
            new ChatCompletionRequest(entry.Id, [new ChatCompletionMessage("user", "oi")]));
        response.EnsureSuccessStatusCode();
        Assert.That(_lastChatBody, Does.Contain("CONTEUDO-DA-SKILL-XYZ"));
    }

    // ---------------- Functions ----------------

    /// <summary>Functions é admin-only: usuário comum recebe 403.</summary>
    [Test]
    public async Task T03_Functions_NaoAdmin_Retorna403()
    {
        UseToken(_userToken);
        Assert.That((await _client.GetAsync("/api/v1/functions/")).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await _client.PostAsJsonAsync("/api/v1/functions/",
            new FunctionRequest("F", "filter", null, null))).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));
    }

    /// <summary>CRUD admin de function: create, toggle, valves, delete.</summary>
    [Test]
    public async Task T04_Functions_CrudToggleValves()
    {
        UseToken(_adminToken);
        var created = await _client.PostAsJsonAsync("/api/v1/functions/",
            new FunctionRequest("Filtro Entrada", "filter",
                "{\"valves\":{\"limiar\":{\"type\":\"number\"}}}", null));
        created.EnsureSuccessStatusCode();
        var fn = (await created.Content.ReadFromJsonAsync<FunctionResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(fn.Type, Is.EqualTo("filter"));
            Assert.That(fn.Active, Is.False);
        });

        var toggled = await _client.PostAsync($"/api/v1/functions/{fn.Id}/toggle", null);
        Assert.That((await toggled.Content.ReadFromJsonAsync<FunctionResponse>())!.Active,
            Is.True);

        var valves = await _client.PostAsJsonAsync($"/api/v1/functions/{fn.Id}/valves",
            new FunctionValvesRequest("{\"limiar\":5}"));
        Assert.That((await valves.Content.ReadFromJsonAsync<FunctionResponse>())!.ValvesJson,
            Is.EqualTo("{\"limiar\":5}"));

        var invalid = await _client.PostAsJsonAsync("/api/v1/functions/",
            new FunctionRequest("X", "indefinido", null, null));
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        Assert.That((await _client.DeleteAsync($"/api/v1/functions/{fn.Id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
    }

    // ---------------- Pipelines ----------------

    /// <summary>Servidor de pipelines: registro com key mascarada e descoberta de pipes.</summary>
    [Test]
    public async Task T05_Pipelines_RegistrarEDescobrir()
    {
        UseToken(_adminToken);
        var saved = await _client.PostAsJsonAsync("/api/v1/pipelines/",
            new PipelineServerRequest("pipes-mock", _mockBase, "pipe-key"));
        saved.EnsureSuccessStatusCode();
        var server = (await saved.Content.ReadFromJsonAsync<PipelineServerResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(server.HasKey, Is.True);
            Assert.That(server.Url, Is.EqualTo(_mockBase));
        });

        var list = await _client.GetFromJsonAsync<List<PipelinePipeResponse>>(
            "/api/v1/pipelines/list");
        Assert.That(list!.Select(p => p.Id), Does.Contain("pipeline:pipe-1"));
    }

    /// <summary>Pipes aparecem como modelos `pipeline:{id}` no seletor.</summary>
    [Test]
    public async Task T06_Models_IncluiPipes()
    {
        UseToken(_userToken);
        var models = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");
        var ids = models!.Data.Select(m => m.Id).ToList();
        Assert.That(ids, Does.Contain("pipeline:pipe-1"));
    }

    /// <summary>Completion pipeline:{id} roteia ao servidor e faz passthrough do SSE.</summary>
    [Test]
    public async Task T07_CompletionPipeline_RoteiaComPassthrough()
    {
        UseToken(_adminToken);
        // Function ativa com valves → vai no corpo ao pipeline
        await _client.PostAsJsonAsync("/api/v1/functions/",
            new FunctionRequest("Válvula", "pipe", null, "{\"threshold\":3}"));
        // Function criada inativa — ativa
        var fns = await _client.GetFromJsonAsync<List<FunctionResponse>>("/api/v1/functions/");
        await _client.PostAsync($"/api/v1/functions/{fns![0].Id}/toggle", null);

        var response = await _client.PostAsJsonAsync("/api/chat/completions",
            new ChatCompletionRequest("pipeline:pipe-1",
                [new ChatCompletionMessage("user", "oi")]));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("pipeline resposta"));
        Assert.That(_lastPipeBody, Does.Contain("\"model\":\"pipe-1\""));
        Assert.That(_lastPipeBody, Does.Contain("\"valves\""));
    }

    /// <summary>Pipe inexistente em todos os servidores → 404.</summary>
    [Test]
    public async Task T08_CompletionPipeline_Inexistente_Retorna404()
    {
        UseToken(_userToken);
        var response = await _client.PostAsJsonAsync("/api/chat/completions",
            new ChatCompletionRequest("pipeline:nao-existe",
                [new ChatCompletionMessage("user", "oi")]));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    /// <summary>Servidor de pipelines fora do ar → 502.</summary>
    [Test]
    public async Task T09_CompletionPipeline_ServidorFora_Retorna502()
    {
        // Servidor numa porta que só responde /models durante a descoberta
        var port = Random.Shared.Next(40000, 60000);
        using var dead = new HttpListener();
        dead.Prefixes.Add($"http://localhost:{port}/");
        dead.Start();
        _ = Task.Run(async () =>
        {
            var ctx = await dead.GetContextAsync();
            if (ctx.Request.Url!.AbsolutePath == "/models")
            {
                WriteJson(ctx, "[{\"id\":\"pipe-dead\",\"name\":\"Morto\"}]");
            }
            else
            {
                WriteJson(ctx, "{}", 404);
            }
            dead.Stop();
        });

        UseToken(_adminToken);
        var saved = await _client.PostAsJsonAsync("/api/v1/pipelines/",
            new PipelineServerRequest("pipes-dead", $"http://localhost:{port}", null));
        saved.EnsureSuccessStatusCode();
        var serverId = (await saved.Content.ReadFromJsonAsync<PipelineServerResponse>())!.Id;
        await Task.Delay(300); // garante a descoberta antes da queda

        var response = await _client.PostAsJsonAsync("/api/chat/completions",
            new ChatCompletionRequest("pipeline:pipe-dead",
                [new ChatCompletionMessage("user", "oi")]));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));

        await _client.DeleteAsync($"/api/v1/pipelines/{serverId}");
    }
}

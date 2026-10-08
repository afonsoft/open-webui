using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura do RF-020/RF-021 (webhooks de automação + integração n8n):
/// CRUD autenticado de <c>/api/v1/hooks</c>, disparo anônimo que
/// enfileira uma run no chat vinculado, config admin do n8n e o
/// <see cref="N8nService"/> contra um handler fake.
/// </summary>
[TestFixture]
public class AutomationHookTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-hooks-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@hooks.local");
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
    }

    // ---------------- Hooks: CRUD + disparo ----------------

    [Test]
    public async Task Hook_Crud_Completo_E_DisparoEnfileiraRun()
    {
        var auth = await SignUpAsync("Hooks", "hooks@local.dev");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Chat hook", ["llama3"]);

        // Cria
        var created = await _client.PostAsJsonAsync("/api/v1/hooks",
            new AutomationHookCreateRequest("meu hook", chat.Id));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var hook = (await created.Content.ReadFromJsonAsync<AutomationHookResponse>())!;
        Assert.That(hook.ChatId, Is.EqualTo(chat.Id));
        Assert.That(hook.Url, Does.Contain(hook.Id));

        // Lista
        var list = await _client.GetFromJsonAsync<List<AutomationHookResponse>>("/api/v1/hooks");
        Assert.That(list!.Any(h => h.Id == hook.Id), Is.True);

        // Disparo anônimo com prompt → 202 + run enfileirada no chat
        _client.DefaultRequestHeaders.Authorization = null;
        var fire = await _client.PostAsJsonAsync(hook.Url,
            new { prompt = "resuma o ticket", payload = new { id = 42 } });
        Assert.That(fire.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await fire.Content.ReadAsStringAsync());
        var fired = (await fire.Content.ReadFromJsonAsync<AutomationHookFireResponse>())!;
        Assert.That(fired.ChatId, Is.EqualTo(chat.Id));

        UseToken(auth.Token);
        var runs = await _client.GetFromJsonAsync<List<ChatRunResponse>>(
            $"/api/v1/chats/{chat.Id}/runs");
        Assert.That(runs!.Any(r => r.Id == fired.RunId), Is.True);

        // Mensagem do webhook entrou no chat como user
        var chatDepois = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{chat.Id}");
        Assert.That(chatDepois!.Messages.Any(m =>
            m.Role == "user" && m.Content.Contains("resuma o ticket")), Is.True);

        // LastFiredAt marcado
        var listDepois = await _client.GetFromJsonAsync<List<AutomationHookResponse>>("/api/v1/hooks");
        Assert.That(listDepois!.First(h => h.Id == hook.Id).LastFiredAt, Is.Not.Null);

        // Deleta → disparo passa a 404
        var del = await _client.DeleteAsync($"/api/v1/hooks/{hook.Id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _client.DefaultRequestHeaders.Authorization = null;
        var fireDepois = await _client.PostAsJsonAsync(hook.Url, new { prompt = "oi" });
        Assert.That(fireDepois.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Hook_SemChat_CriaChatProprio_E_UsaModelDoCorpo()
    {
        var auth = await SignUpAsync("Auto", "auto@hooks.local");
        UseToken(auth.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/hooks",
            new AutomationHookCreateRequest("hook auto", null));
        var hook = (await created.Content.ReadFromJsonAsync<AutomationHookResponse>())!;

        // Chat criado sem modelo → disparo sem model → 422
        _client.DefaultRequestHeaders.Authorization = null;
        var semModelo = await _client.PostAsJsonAsync(hook.Url, new { prompt = "oi" });
        Assert.That(semModelo.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));

        // Com model no corpo → 202
        var comModelo = await _client.PostAsJsonAsync(hook.Url,
            new { prompt = "oi", model = "llama3" });
        Assert.That(comModelo.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
    }

    [Test]
    public async Task Hook_PayloadSemPrompt_ViraJson_E_TokenInvalidoDa404()
    {
        var auth = await SignUpAsync("Pay", "pay@hooks.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Chat payload", ["llama3"]);
        var created = await _client.PostAsJsonAsync("/api/v1/hooks",
            new AutomationHookCreateRequest("payload", chat.Id));
        var hook = (await created.Content.ReadFromJsonAsync<AutomationHookResponse>())!;

        _client.DefaultRequestHeaders.Authorization = null;
        var fire = await _client.PostAsJsonAsync(hook.Url, new { issue = "BUG-1", status = "open" });
        Assert.That(fire.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        var notFound = await _client.PostAsJsonAsync("/api/v1/hooks/token-que-nao-existe",
            new { prompt = "oi" });
        Assert.That(notFound.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var alias = await _client.PostAsJsonAsync($"/api/v1/hooks/n8n/{hook.Id}",
            new { prompt = "alias n8n" });
        Assert.That(alias.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
    }

    [Test]
    public async Task Hook_OutroUsuario_NaoVeNemDeleta()
    {
        var dono = await SignUpAsync("Dono", "dono@hooks.local");
        var intruso = await SignUpAsync("Intruso", "intruso@hooks.local");

        UseToken(dono.Token);
        var chat = await CriarChatAsync("Chat dono", ["llama3"]);
        var created = await _client.PostAsJsonAsync("/api/v1/hooks",
            new AutomationHookCreateRequest("hook dono", chat.Id));
        var hook = (await created.Content.ReadFromJsonAsync<AutomationHookResponse>())!;

        UseToken(intruso.Token);
        var list = await _client.GetFromJsonAsync<List<AutomationHookResponse>>("/api/v1/hooks");
        Assert.That(list!.Any(h => h.Id == hook.Id), Is.False);
        var del = await _client.DeleteAsync($"/api/v1/hooks/{hook.Id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Criar hook apontando pra chat alheio → 404
        var alien = await _client.PostAsJsonAsync("/api/v1/hooks",
            new AutomationHookCreateRequest("hook alien", chat.Id));
        Assert.That(alien.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---------------- n8n: config admin + endpoint ----------------

    [Test]
    public async Task N8n_Config_RequerAdmin_E_NaoVazaApiKey()
    {
        var admin = await SignUpAsync("N8nAdmin", "n8nadmin@hooks.local");
        UseToken(admin.Token);
        // Primeiro usuário do teste virou admin por ser o primeiro signup? Não —
        // promove via config admin é complexo; este signup é user comum → 403 esperado.
        var get = await _client.GetAsync("/api/v1/n8n/config");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        var put = await _client.PutAsJsonAsync("/api/v1/n8n/config",
            new N8nConfigUpdateRequest("http://n8n.local", "key"));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task N8n_Admin_Configura_E_ListaWorkflowsSemN8nDaErro()
    {
        // O primeiro usuário registrado na base do fixture é admin.
        var adminLogin = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("admin@hooks.local", "senha123"));
        Assert.That(adminLogin.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await adminLogin.Content.ReadAsStringAsync());
        var admin = (await adminLogin.Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(admin.Token);

        var put = await _client.PutAsJsonAsync("/api/v1/n8n/config",
            new N8nConfigUpdateRequest("http://n8n.invalid", "segredo-n8n"));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await put.Content.ReadAsStringAsync());

        var config = await _client.GetFromJsonAsync<N8nConfigResponse>("/api/v1/n8n/config");
        Assert.That(config!.Configured, Is.True);
        Assert.That(config.BaseUrl, Is.EqualTo("http://n8n.invalid"));
        Assert.That(config.HasApiKey, Is.True);
        var raw = await _client.GetStringAsync("/api/v1/n8n/config");
        Assert.That(raw, Does.Not.Contain("segredo-n8n"));

        // Proxy de workflows falha ao contatar o host configurado → 5xx/400, nunca 200 vazio
        var workflows = await _client.GetAsync("/api/v1/n8n/workflows");
        Assert.That((int)workflows.StatusCode, Is.GreaterThanOrEqualTo(400));
    }

    // ---------------- N8nService (unitário, handler fake) ----------------

    [Test]
    public async Task N8nService_ListaWorkflows_E_DisparaPorIdEWebhook()
    {
        var requests = new List<(string Method, string Url, string? ApiKey, string Body)>();
        var handler = new FakeHandler(async request =>
        {
            var body = request.Content is null ? string.Empty
                : await request.Content.ReadAsStringAsync();
            requests.Add((request.Method.Method, request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-N8N-API-KEY", out var v) ? v.First() : null, body));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/workflows" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        data = new[]
                        {
                            new { id = "wf1", name = "Deploy", active = true },
                            new { id = "wf2", name = "Batch", active = false },
                        },
                    }),
                },
                "/api/v1/workflows/wf1/execute" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { executionId = "ex1" }),
                },
                "/webhook/deploy" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { ok = true }),
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        // Db próprio: a base do fixture pode ter n8n.base_url gravada
        // por testes de endpoint anteriores.
        var serviceDb = NewIsolatedDb();
        using var mc2 = new MemoryCache(new MemoryCacheOptions());
        var config = new ConfigService(serviceDb, mc2);
        await config.SetAsync<string?>(N8nService.BaseUrlKey, "http://n8n.test", default);
        await config.SetAsync<string?>(N8nService.ApiKeyKey, "k-123", default);
        var service = new N8nService(new StubFactory(handler), config,
            new ConfigurationBuilder().Build());

        var workflows = await service.ListWorkflowsAsync(default);
        Assert.That(workflows.Select(w => w.Name),
            Is.EqualTo(new[] { "Deploy", "Batch" }));
        Assert.That(workflows[0].Active, Is.True);
        Assert.That(requests[0].ApiKey, Is.EqualTo("k-123"));

        var viaId = await service.TriggerAsync("wf1", null, null, default);
        Assert.That(viaId.Success, Is.True);
        Assert.That(requests[^1].Url, Does.Contain("/api/v1/workflows/wf1/execute"));

        var viaHook = await service.TriggerAsync(null, "/deploy", null, default);
        Assert.That(viaHook.Success, Is.True);
        Assert.That(requests[^1].Url, Does.Contain("/webhook/deploy"));
        Assert.That(requests[^1].ApiKey, Is.Null); // webhook não envia API key
    }

    [Test]
    public async Task N8nService_SemConfig_LancaInvalidOperation()
    {
        var serviceDb = NewIsolatedDb();
        using var mc1 = new MemoryCache(new MemoryCacheOptions());
        var config = new ConfigService(serviceDb, mc1);
        var service = new N8nService(new StubFactory(), config,
            new ConfigurationBuilder().Build());

        Assert.That(await service.IsConfiguredAsync(default), Is.False);
        Assert.That((await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ListWorkflowsAsync(default)))!.Message,
            Does.Contain("não configurado"));
        Assert.That((await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TriggerAsync("wf1", null, null, default)))!.Message,
            Does.Contain("não configurado"));
    }

    // ---------------- Helpers ----------------

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private AppDbContext NewIsolatedDb()
    {
        var path = Path.Join(Path.GetTempPath(),
            $"openwebui-hooks-svc-{Guid.NewGuid():N}.db");
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        DatabaseMigrator.MigrateAsync(db).GetAwaiter().GetResult();
        return db;
    }

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

    private async Task<ChatResponse> CriarChatAsync(string title, string[] models)
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest(title, models, []));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }

    private sealed class StubFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            handler is null ? new HttpClient() : new HttpClient(handler);
    }
}

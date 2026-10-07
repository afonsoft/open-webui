using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobre os caminhos das runs/notificações que os testes principais não
/// tocam (SPEC-20261007-chat-detached-runs + notifications): rota de
/// pipeline inexistente, loop de tools sem provider, webhooks CRUD+test,
/// sweep de runs órfãs, stop de run finalizada e envio real do
/// <c>WebPushSender</c> contra um push service mock (HttpListener).
/// </summary>
[TestFixture]
public class RunCoverageTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _mockBase = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private readonly ConcurrentQueue<string> _received = new();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-cov-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _mockBase = StartMock();

        var admin = await SignUpAsync("Admin", "admin@cov.local");
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
        _mockCts.Cancel();
        _mock.Stop();
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
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
            catch (ObjectDisposedException)
            {
                return;
            }

            string body = string.Empty;
            if (ctx.Request.HasEntityBody)
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                body = await reader.ReadToEndAsync(ct);
            }
            _received.Enqueue($"{ctx.Request.HttpMethod} {ctx.Request.Url!.AbsolutePath} {body[..Math.Min(body.Length, 120)]}");

            // Webhook e push service: 201 = entregue.
            var bytes = Encoding.UTF8.GetBytes("{}");
            ctx.Response.StatusCode = 201;
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
        }
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

    private async Task<ChatResponse> CriarChatAsync()
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Chat cov", ["llama3"], []));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    private async Task<ChatRunResponse> EnfileirarAsync(
        string chatId, string content, string model = "llama3",
        IReadOnlyList<string>? toolIds = null)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chatId}/messages",
            new EnqueueChatRunRequest(content, model, ToolIds: toolIds));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChatRunResponse>())!;
    }

    private async Task<ChatRunResponse> AguardarFinalAsync(string chatId, string runId)
    {
        var limite = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < limite)
        {
            var run = await _client.GetFromJsonAsync<ChatRunResponse>(
                $"/api/v1/chats/{chatId}/runs/{runId}");
            Assert.That(run, Is.Not.Null);
            if (run!.Status is not ("queued" or "running"))
            {
                return run;
            }
            await Task.Delay(150);
        }
        Assert.Fail($"Run {runId} não finalizou em 60s.");
        return null!;
    }

    // ---- Runs: caminhos do executor ----

    [Test]
    public async Task Pipeline_PipeInexistente_RunFalhaComErro()
    {
        var auth = await SignUpAsync("Pipe", "pipe@cov.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();

        var run = await EnfileirarAsync(chat.Id, "oi", model: "pipeline:naoexiste");
        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"));

        // O erro sai no stream de attach (replay do backlog).
        using var response = await _client.GetAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/stream?lastSeq=0");
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("naoexiste"));
    }

    [Test]
    public async Task Tools_SemProvider_RunFalhaNoLoop()
    {
        var auth = await SignUpAsync("Tool", "tool@cov.local");
        UseToken(auth.Token);
        var spec = "{\"type\":\"function\",\"function\":{\"name\":\"x\"}}";
        var toolResponse = await _client.PostAsJsonAsync("/api/v1/tools/",
            new ToolUpsertRequest("X", "x", spec, $"{_mockBase}/tool/x"));
        Assert.That(toolResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var tool = await toolResponse.Content.ReadFromJsonAsync<ToolResponse>();

        var chat = await CriarChatAsync();
        var run = await EnfileirarAsync(chat.Id, "usa tool", toolIds: [tool!.Id]);
        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("failed"), final.Error);
    }

    [Test]
    public async Task Stop_RunFinalizada_RetornaConflito()
    {
        var auth = await SignUpAsync("Stop", "stop@cov.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();
        var run = await EnfileirarAsync(chat.Id, "oi");
        await AguardarFinalAsync(chat.Id, run.Id);

        var stop = await _client.PostAsync($"/api/v1/chats/{chat.Id}/runs/{run.Id}/stop", null);
        Assert.That(stop.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    // ---- Webhooks ----

    [Test]
    public async Task Webhook_CrudETest_Completos()
    {
        var auth = await SignUpAsync("Hook", "hook@cov.local");
        UseToken(auth.Token);

        var save = await _client.PostAsJsonAsync("/api/v1/notifications/webhook",
            new NotificationWebhookRequest($"{_mockBase}/hook", ["run.completed"]));
        Assert.That(save.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await save.Content.ReadAsStringAsync());

        // Upsert: mesmo webhook atualizado com novos eventos.
        var update = await _client.PostAsJsonAsync("/api/v1/notifications/webhook",
            new NotificationWebhookRequest($"{_mockBase}/hook", ["run.completed", "run.failed"], false));
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var get = await _client.GetAsync("/api/v1/notifications/webhook");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Dispara o teste — o NotificationService faz POST assinado no mock.
        var test = await _client.PostAsync("/api/v1/notifications/webhook/test", null);
        Assert.That(test.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var result = await test.Content.ReadFromJsonAsync<WebhookTestResponse>();
        Assert.That(result!.Ok, Is.True, $"status {result.StatusCode}");
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (_received.IsEmpty && DateTime.UtcNow < limite)
        {
            await Task.Delay(100);
        }
        Assert.That(_received.Any(r => r.Contains("/hook")), Is.True, "webhook não chegou no mock");

        var del = await _client.DeleteAsync("/api/v1/notifications/webhook");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var depois = await _client.GetAsync("/api/v1/notifications/webhook");
        Assert.That(depois.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Webhook_Admin_GlobalERestricoes()
    {
        var auth = await SignUpAsync("HookAdmin", $"hookadmin-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);

        // Usuário comum → 403 no escopo admin.
        var forbidden = await _client.PostAsJsonAsync("/api/v1/notifications/admin/webhook",
            new NotificationWebhookRequest($"{_mockBase}/global", ["x"]));
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        // Re-login como o admin criado no setup (primeiro signup = admin).
        var admin = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("admin@cov.local", "senha123"));
        Assert.That(admin.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var adminAuth = await admin.Content.ReadFromJsonAsync<AuthResponse>();

        UseToken(adminAuth!.Token);
        var save = await _client.PostAsJsonAsync("/api/v1/notifications/admin/webhook",
            new NotificationWebhookRequest($"{_mockBase}/global", ["run.completed"]));
        Assert.That(save.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await save.Content.ReadAsStringAsync());
        var del = await _client.DeleteAsync("/api/v1/notifications/admin/webhook");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Validações: URL inválida, webhook inexistente.
        var bad = await _client.PostAsJsonAsync("/api/v1/notifications/webhook",
            new NotificationWebhookRequest("notaurl", ["x"]));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var inexistente = await _client.DeleteAsync("/api/v1/notifications/admin/webhook");
        Assert.That(inexistente.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- Web Push real (sender + VAPID gerado/persistido) ----

    [Test]
    public async Task WebPush_SenderReal_MockRecebeEntrega()
    {
        var auth = await SignUpAsync("PushReal", $"pushreal-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);

        // Chaves válidas (EC P-256 + auth de 16 bytes, base64url) geradas em build.
        var endpoint = $"{_mockBase}/push/{Guid.NewGuid():N}";
        var sub = await _client.PostAsJsonAsync("/api/v1/notifications/push/subscriptions",
            new PushSubscriptionRequest(endpoint,
                new PushSubscriptionKeys(
                    "BMJSvet1XxjmdWY2-z17oRIgoIBYnx5x0OowCwqn_gMushfv21DFZYdnC_-8G3AI4lyimQJHoVqZS-p0hPrEAWg",
                    "WOb6rGjrpzlZZnvkDhkg6A"),
                "nunit"));
        Assert.That(sub.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await sub.Content.ReadAsStringAsync());

        // VAPID key é gerada e persistida na primeira chamada; a segunda reusa.
        var k1 = await _client.GetFromJsonAsync<VapidPublicKeyResponse>(
            "/api/v1/notifications/push/vapid-key");
        var k2 = await _client.GetFromJsonAsync<VapidPublicKeyResponse>(
            "/api/v1/notifications/push/vapid-key");
        Assert.That(k1!.PublicKey, Is.EqualTo(k2!.PublicKey));

        var chat = await CriarChatAsync();
        var run = await EnfileirarAsync(chat.Id, "notifica");
        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("failed")); // sem provider — só o push importa

        var limite = DateTime.UtcNow.AddSeconds(15);
        while (!_received.Any(r => r.Contains("/push/")) && DateTime.UtcNow < limite)
        {
            await Task.Delay(200);
        }
        Assert.That(_received.Any(r => r.StartsWith("POST /push/", StringComparison.Ordinal)),
            Is.True, "push não chegou no mock");
    }

    [Test]
    public async Task PushSubscription_Delete_SemEndpoint_400()
    {
        var auth = await SignUpAsync("PushDel", $"pushdel-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);
        var del = await _client.DeleteAsync("/api/v1/notifications/push/subscriptions");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---- Dispatcher: sweep de runs órfãs no boot ----

    [Test]
    public async Task Dispatcher_Boot_MarcaRunOrfaComoInterrompida()
    {
        var auth = await SignUpAsync("Orpha", $"orpha-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();

        // Semeia uma run "running" direto no banco — como se o servidor
        // tivesse caído no meio da execução.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChatRuns.Add(new ChatRun
            {
                ChatId = chat.Id,
                UserId = auth.User.Id,
                Model = "llama3",
                Status = ChatRunStatus.Running,
                RequestJson = "{\"model\":\"llama3\",\"messages\":[]}",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            await db.SaveChangesAsync();
        }
        var orfaId = await DbChatRunIdAsync(chat.Id);

        // Um segundo host sobre o MESMO banco executa o sweep no boot.
        using var factory2 = new WebApplicationFactory<Program>();
        _ = factory2.Server; // força o boot (dispatcher + sweep)
        using var client2 = factory2.CreateClient();
        client2.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.Token);

        var limite = DateTime.UtcNow.AddSeconds(30);
        ChatRunResponse? run = null;
        while (DateTime.UtcNow < limite)
        {
            run = await _client.GetFromJsonAsync<ChatRunResponse>(
                $"/api/v1/chats/{chat.Id}/runs/{orfaId}");
            if (run!.Status == "interrupted")
            {
                break;
            }
            await Task.Delay(200);
        }
        Assert.That(run!.Status, Is.EqualTo("interrupted"));
    }

    private async Task<string> DbChatRunIdAsync(string chatId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ChatRuns.OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(r => r.ChatId == chatId);
        return run!.Id;
    }
}

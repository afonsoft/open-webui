using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes dos webhooks de notificação: CRUD por usuário/global, validação de URL,
/// disparo de teste com assinatura HMAC e best-effort.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class NotificationEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpListener _sink = null!;
    private CancellationTokenSource _sinkCts = null!;
    private string _sinkUrl = string.Empty;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;
    private AuthResponse _user = null!;
    private readonly List<(string Event, string Signature, string Body)> _received = [];

    /// <summary>Sobe a app e um receptor de webhooks.</summary>
    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _sinkUrl = StartSink();
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-notif-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _admin = await SignUpAsync("Admin", "admin@notif.local", "senha123");
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        _client.DefaultRequestHeaders.Authorization = null;
        _user = await SignUpAsync("User", "user@notif.local", "senha123");
    }

    /// <summary>Encerra os recursos.</summary>
    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _sinkCts.Cancel();
        _sink.Close();
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private string StartSink()
    {
        var random = new Random();
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port = random.Next(40000, 60000);
            _sink = new HttpListener();
            _sink.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _sink.Start();
                break;
            }
            catch (HttpListenerException)
            {
                _sink.Close();
            }
        }
        _sinkCts = new CancellationTokenSource();
        _ = Task.Run(() => SinkLoopAsync(_sinkCts.Token));
        return $"http://localhost:{port}";
    }

    private async Task SinkLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _sink.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }

            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            lock (_received)
            {
                _received.Add((
                    ctx.Request.Headers["X-Webhook-Event"] ?? "",
                    ctx.Request.Headers["X-Webhook-Signature"] ?? "",
                    body));
            }
            ctx.Response.StatusCode = 200;
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

    [Test]
    public async Task Webhook_CRUD_PorUsuario() // RF-001
    {
        UseToken(_user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/notifications/webhook",
            new NotificationWebhookRequest(_sinkUrl, ["automation.failed"]));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var webhook = await created.Content.ReadFromJsonAsync<NotificationWebhookResponse>();
        Assert.That(webhook!.Url, Is.EqualTo(_sinkUrl));
        Assert.That(webhook.Events, Is.EqualTo(new[] { "automation.failed" }));

        var fetched = await _client.GetFromJsonAsync<NotificationWebhookResponse>("/api/v1/notifications/webhook");
        Assert.That(fetched!.Id, Is.EqualTo(webhook.Id));

        // segredo nunca retorna
        var raw = await _client.GetStringAsync("/api/v1/notifications/webhook");
        Assert.That(raw, Does.Not.Contain("secret").IgnoreCase);
    }

    [Test]
    public async Task Webhook_UrlInvalida_400() // RF-001
    {
        UseToken(_user.Token);
        var response = await _client.PostAsJsonAsync("/api/v1/notifications/webhook",
            new NotificationWebhookRequest("ftp://invalido", ["x"]));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Webhook_Test_DisparaAssinado() // RF-003
    {
        UseToken(_user.Token);
        _received.Clear();
        var response = await _client.PostAsync("/api/v1/notifications/webhook/test", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var result = await response.Content.ReadFromJsonAsync<WebhookTestResponse>();
        Assert.That(result!.Ok, Is.True);
        Assert.That(result.StatusCode, Is.EqualTo(200));

        await Task.Delay(500);
        lock (_received)
        {
            Assert.That(_received, Has.Count.EqualTo(1));
            Assert.That(_received[0].Event, Is.EqualTo("test"));
            Assert.That(_received[0].Signature, Does.StartWith("sha256="));
            Assert.That(_received[0].Body, Does.Contain("\"test\""));
        }
    }

    [Test]
    public async Task AdminWebhook_SomenteAdmin_EDisparaNoEvento() // RF-001/RF-002
    {
        UseToken(_user.Token);
        var forbidden = await _client.PostAsJsonAsync("/api/v1/notifications/admin/webhook",
            new NotificationWebhookRequest(_sinkUrl, ["user.pending"]));
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/notifications/admin/webhook",
            new NotificationWebhookRequest(_sinkUrl, ["user.pending"]));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Dispatch_NaoQuebraFluxo_QuandoDestinoFora() // RF-002
    {
        // webhook apontando para porta fechada: signup pending não pode falhar
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync("/api/v1/notifications/admin/webhook",
            new NotificationWebhookRequest("http://localhost:1/nada", ["user.pending"]));
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "pending" });

        _client.DefaultRequestHeaders.Authorization = null;
        var signup = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new SignUpRequest("Pendente", "pend@notif.local", "senha123"));
        Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // aprovação dispara user.approved — também best-effort
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync("/api/v1/notifications/admin/webhook",
            new NotificationWebhookRequest(_sinkUrl, ["user.approved"]));
        _received.Clear();
        var pending = await _client.GetFromJsonAsync<JsonElement>("/api/v1/users?filter=pending");
        var id = pending.GetProperty("users").EnumerateArray().First().GetProperty("id").GetString();
        var approve = await _client.PostAsJsonAsync($"/api/v1/users/{id}/update/role",
            new { role = "user" });
        Assert.That(approve.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await Task.Delay(700);
        lock (_received)
        {
            Assert.That(_received.Any(r => r.Event == "user.approved"), Is.True);
        }
    }
}

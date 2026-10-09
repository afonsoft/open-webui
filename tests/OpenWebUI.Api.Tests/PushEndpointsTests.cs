using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobre os endpoints de subscription Web Push
/// (SPEC-20261007-chat-notifications RF-004): chave VAPID, upsert por
/// endpoint e delete escopado ao dono.
/// </summary>
[TestFixture, IsolateEnvironment]
public sealed class PushEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(
            Path.GetTempPath(), $"openwebui-push-tests-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        // Bootstrap: primeiro usuário vira admin; sem config os demais
        // signups ficam pending (401) — libera "user" como papel padrão.
        var admin = await SignUpAsync("PushBootstrap");
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        updated.EnsureSuccessStatusCode();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", null);
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Limpeza best-effort — subscription pode nem existir.
        }
    }

    [SetUp]
    public async Task SetUp()
    {
        var admin = await SignUpAsync($"PushAdmin-{Guid.NewGuid():N}");
        UseToken(admin.Token);
    }

    private async Task<AuthResponse> SignUpAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name, email = $"{name}@push.local", password = "Senha1234!" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    [Test]
    public async Task VapidKey_RetornaChavePublica()
    {
        var key = await _client.GetFromJsonAsync<VapidPublicKeyResponse>(
            "/api/v1/notifications/push/vapid-key");
        // Sem VAPID__* no ambiente o servidor gera e persiste o par uma vez.
        Assert.That(key!.PublicKey, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Subscription_SaveEUpsertPorEndpoint()
    {
        var request = new PushSubscriptionRequest(
            "https://push.example/sub/1",
            new PushSubscriptionKeys("p256dh-key", "auth-secret"),
            "nunit-agent");

        var save = await _client.PostAsJsonAsync(
            "/api/v1/notifications/push/subscriptions", request);
        Assert.That(save.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Upsert com o mesmo endpoint não duplica (rotaciona chaves).
        var rotated = await _client.PostAsJsonAsync(
            "/api/v1/notifications/push/subscriptions",
            request with { Keys = new PushSubscriptionKeys("p256dh-2", "auth-2") });
        Assert.That(rotated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task VapidKey_Env_PrevaleceSobrePersistida()
    {
        Environment.SetEnvironmentVariable("VAPID__PUBLIC_KEY", "env-public");
        Environment.SetEnvironmentVariable("VAPID__PRIVATE_KEY", "env-private");
        try
        {
            var key = await _client.GetFromJsonAsync<VapidPublicKeyResponse>(
                "/api/v1/notifications/push/vapid-key");
            Assert.That(key!.PublicKey, Is.EqualTo("env-public"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAPID__PUBLIC_KEY", null);
            Environment.SetEnvironmentVariable("VAPID__PRIVATE_KEY", null);
        }
    }

    [Test]
    public async Task WebPush_EndpointMorto_NotificaSemQuebrarRun()
    {
        // Subscription apontando para um push service inalcançável: a run
        // falha rápido (sem provider) e o notifier tenta enviar — erro é
        // absorvido, run finaliza normalmente, subscription não é prunada
        // (não é 404/410).
        var endpoint = "http://localhost:1/push";
        await _client.PostAsJsonAsync("/api/v1/notifications/push/subscriptions",
            new PushSubscriptionRequest(endpoint, new PushSubscriptionKeys("k", "a")));

        var run = await EnfileirarAsync("notifique-me");
        var final = await AguardarFinalAsync(run.ChatId, run.Id);
        Assert.That(final.Status, Is.EqualTo("failed"));

        var del = await _client.DeleteAsync(
            $"/api/v1/notifications/push/subscriptions?endpoint={Uri.EscapeDataString(endpoint)}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task WebPush_Endpoint404_SubscriptionPrunada()
    {
        // Sender fake devolvendo 404: o notifier deve prunar a subscription.
        var factory404 = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<OpenWebUI.Application.Interfaces.IWebPushSender>();
                services.AddScoped<OpenWebUI.Application.Interfaces.IWebPushSender>(
                    _ => new FakeSender(404));
            }));
        using var client = factory404.CreateClient();
        var user = await SignUpAsync(client, $"Push404-{Guid.NewGuid():N}");
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);

        var endpoint = $"https://push.example/dead/{Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/api/v1/notifications/push/subscriptions",
            new PushSubscriptionRequest(endpoint, new PushSubscriptionKeys("k", "a")));

        var run = await EnfileirarAsync(client, "prune-me");
        var final = await AguardarFinalAsync(client, run.ChatId, run.Id);
        Assert.That(final.Status, Is.EqualTo("failed"));

        // O prune acontece no notifier, assíncrono ao status terminal da run.
        var deleteStatus = HttpStatusCode.OK;
        for (var i = 0; i < 40 && deleteStatus == HttpStatusCode.OK; i++)
        {
            var del = await client.DeleteAsync(
                $"/api/v1/notifications/push/subscriptions?endpoint={Uri.EscapeDataString(endpoint)}");
            deleteStatus = del.StatusCode;
            if (deleteStatus != HttpStatusCode.NotFound)
            {
                await Task.Delay(150);
            }
        }
        Assert.That(deleteStatus, Is.EqualTo(HttpStatusCode.NotFound));
    }

    private sealed class FakeSender(int httpStatus)
        : OpenWebUI.Application.Interfaces.IWebPushSender
    {
        public Task<OpenWebUI.Application.Interfaces.IWebPushSender.Result> SendAsync(
            string endpoint, string p256dh, string auth,
            string payloadJson, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenWebUI.Application.Interfaces.IWebPushSender.Result(
                Sent: false, HttpStatus: httpStatus, Error: "fake"));
    }

    private async Task<ChatRunResponse> EnfileirarAsync(string content) =>
        await EnfileirarAsync(_client, content);

    private async Task<ChatRunResponse> EnfileirarAsync(HttpClient client, string content)
    {
        var chat = await client.PostAsJsonAsync("/api/v1/chats/",
            new { title = "Push run", models = Array.Empty<string>(),
                messages = Array.Empty<object>() });
        chat.EnsureSuccessStatusCode();
        var created = await chat.Content.ReadFromJsonAsync<ChatResponse>();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/chats/{created!.Id}/messages",
            new EnqueueChatRunRequest(content, "llama3"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChatRunResponse>())!;
    }

    private async Task<ChatRunResponse> AguardarFinalAsync(
        HttpClient client, string chatId, string runId)
    {
        for (var i = 0; i < 400; i++)
        {
            var run = await client.GetFromJsonAsync<ChatRunResponse>(
                $"/api/v1/chats/{chatId}/runs/{runId}");
            if (run!.Status is not ("queued" or "running"))
            {
                return run;
            }

            await Task.Delay(150);
        }

        Assert.Fail($"Run {runId} não finalizou em 60s.");
        return null!;
    }

    private Task<ChatRunResponse> AguardarFinalAsync(string chatId, string runId) =>
        AguardarFinalAsync(_client, chatId, runId);

    private static async Task<AuthResponse> SignUpAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name, email = $"{name}@push.local", password = "Senha1234!" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    [Test]
    public async Task Subscription_Delete_EscopadoAoDono()
    {
        var dono = await SignUpAsync($"PushDono-{Guid.NewGuid():N}");
        var outro = await SignUpAsync($"PushOutro-{Guid.NewGuid():N}");
        var endpoint = $"https://push.example/sub/{Guid.NewGuid():N}";

        UseToken(dono.Token);
        await _client.PostAsJsonAsync("/api/v1/notifications/push/subscriptions",
            new PushSubscriptionRequest(
                endpoint, new PushSubscriptionKeys("k", "a")));

        // Outro usuário não pode apagar a subscription do dono.
        UseToken(outro.Token);
        var alien = await _client.DeleteAsync(
            $"/api/v1/notifications/push/subscriptions?endpoint={Uri.EscapeDataString(endpoint)}");
        Assert.That(alien.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // O dono remove a própria subscription.
        UseToken(dono.Token);
        var del = await _client.DeleteAsync(
            $"/api/v1/notifications/push/subscriptions?endpoint={Uri.EscapeDataString(endpoint)}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}

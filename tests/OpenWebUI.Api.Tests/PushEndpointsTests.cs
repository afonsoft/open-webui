using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobre os endpoints de subscription Web Push
/// (SPEC-20261007-chat-notifications RF-004): chave VAPID, upsert por
/// endpoint e delete escopado ao dono.
/// </summary>
[TestFixture]
public sealed class PushEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Combine(
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

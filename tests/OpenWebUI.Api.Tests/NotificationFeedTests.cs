using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Feed in-app de notificações (SPEC-20261009-notification-feed D3):
/// lista paginada com filtro de não-lidas, mark read/read-all, isolamento
/// por usuário e persistência da linha junto do push ao fim da run.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class NotificationFeedTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(
            Path.GetTempPath(), $"openwebui-feed-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        // Bootstrap: primeiro usuário vira admin e libera "user" como papel padrão.
        var admin = await SignUpAsync($"FeedAdmin-{Guid.NewGuid():N}");
        UseToken(admin.Token);
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        updated.EnsureSuccessStatusCode();
        _client.DefaultRequestHeaders.Authorization = null;
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
            // Limpeza best-effort.
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name, email = $"{name}@feed.local", password = "Senha1234!" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    private async Task SeedAsync(params Notification[] notifications)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Notifications.AddRange(notifications);
        await db.SaveChangesAsync();
    }

    private static Notification Item(string userId, string title, bool read = false, long? createdAt = null) =>
        new()
        {
            UserId = userId,
            Kind = "run.completed",
            Title = title,
            Body = $"body {title}",
            Link = "/c/abc",
            ReadAt = read ? 1700000001 : null,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

    [Test]
    public async Task List_Paginado_PorUsuario()
    {
        var user = await SignUpAsync($"FeedPage-{Guid.NewGuid():N}");
        UseToken(user.Token);
        var base_ = 1700000000L;
        await SeedAsync(Enumerable.Range(0, 25)
            .Select(i => Item(user.User.Id, $"n{i}", createdAt: base_ + i))
            .ToArray());

        var page1 = await _client.GetFromJsonAsync<NotificationPageResponse>(
            "/api/v1/notifications?page=1&pageSize=20");
        Assert.Multiple(() =>
        {
            Assert.That(page1!.Items, Has.Count.EqualTo(20));
            Assert.That(page1.Total, Is.GreaterThanOrEqualTo(25));
            Assert.That(page1.Page, Is.EqualTo(1));
            // Mais recente primeiro.
            Assert.That(page1.Items[0].Title, Is.EqualTo("n24"));
        });

        var page2 = await _client.GetFromJsonAsync<NotificationPageResponse>(
            "/api/v1/notifications?page=2&pageSize=20");
        Assert.That(page2!.Items.Count, Is.GreaterThanOrEqualTo(5));
    }

    [Test]
    public async Task List_FiltroNaoLidas_EUnreadCount()
    {
        var user = await SignUpAsync($"FeedUnread-{Guid.NewGuid():N}");
        UseToken(user.Token);
        await SeedAsync(
            Item(user.User.Id, "lida-1", read: true),
            Item(user.User.Id, "lida-2", read: true),
            Item(user.User.Id, "nova-1"),
            Item(user.User.Id, "nova-2"));

        var page = await _client.GetFromJsonAsync<NotificationPageResponse>(
            "/api/v1/notifications?unread=true");
        Assert.Multiple(() =>
        {
            Assert.That(page!.Items, Has.Count.EqualTo(2), "só não-lidas");
            Assert.That(page.Items.All(i => i.ReadAt is null), Is.True);
            Assert.That(page.Unread, Is.EqualTo(2), "badge = não-lidas do usuário");
        });
    }

    [Test]
    public async Task Read_MarcaUma_E_Idempotente()
    {
        var user = await SignUpAsync($"FeedRead-{Guid.NewGuid():N}");
        UseToken(user.Token);
        var item = Item(user.User.Id, "alvo");
        await SeedAsync(item);

        var read = await _client.PostAsync($"/api/v1/notifications/{item.Id}/read", null);
        Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var again = await _client.PostAsync($"/api/v1/notifications/{item.Id}/read", null);
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK), "reler não é 404");

        var page = await _client.GetFromJsonAsync<NotificationPageResponse>(
            "/api/v1/notifications?unread=true");
        Assert.That(page!.Items.All(i => i.Id != item.Id), Is.True);
    }

    [Test]
    public async Task ReadAll_MarcaTodasDoUsuario()
    {
        var user = await SignUpAsync($"FeedAll-{Guid.NewGuid():N}");
        var outro = await SignUpAsync($"FeedAllOutro-{Guid.NewGuid():N}");
        UseToken(user.Token);
        var alheia = Item(outro.User.Id, "alheia");
        await SeedAsync(
            Item(user.User.Id, "a"), Item(user.User.Id, "b"), alheia);

        var readAll = await _client.PostAsync("/api/v1/notifications/read-all", null);
        Assert.That(readAll.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var page = await _client.GetFromJsonAsync<NotificationPageResponse>(
            "/api/v1/notifications");
        Assert.That(page!.Unread, Is.EqualTo(0));

        // A do outro usuário continua não-lida.
        UseToken(outro.Token);
        var alienPage = await _client.GetFromJsonAsync<NotificationPageResponse>(
            "/api/v1/notifications?unread=true");
        Assert.That(alienPage!.Items.Any(i => i.Id == alheia.Id), Is.True);
    }

    [Test]
    public async Task Read_DeOutroUsuario_404()
    {
        var dono = await SignUpAsync($"FeedDono-{Guid.NewGuid():N}");
        var outro = await SignUpAsync($"FeedOutro-{Guid.NewGuid():N}");
        var item = Item(dono.User.Id, "só do dono");
        await SeedAsync(item);

        UseToken(outro.Token);
        var alien = await _client.PostAsync($"/api/v1/notifications/{item.Id}/read", null);
        Assert.That(alien.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task List_SemAuth_401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var response = await _client.GetAsync("/api/v1/notifications");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task RunFinalizada_PersisteNotificacao()
    {
        // O evento que dispara push também grava linha no feed (mesmo sem
        // subscription VAPID): run falha rápido sem provider e o notifier
        // persiste — assíncrono ao status terminal, então poll no GET.
        var user = await SignUpAsync($"FeedRun-{Guid.NewGuid():N}");
        UseToken(user.Token);

        var chat = await _client.PostAsJsonAsync("/api/v1/chats/",
            new { title = "Feed run", models = Array.Empty<string>(),
                messages = Array.Empty<object>() });
        chat.EnsureSuccessStatusCode();
        var created = await chat.Content.ReadFromJsonAsync<ChatResponse>();
        var enqueue = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{created!.Id}/messages",
            new EnqueueChatRunRequest("notifique-me", "llama3"));
        enqueue.EnsureSuccessStatusCode();
        var run = await enqueue.Content.ReadFromJsonAsync<ChatRunResponse>();

        var final = await AguardarFinalAsync(created.Id, run!.Id);
        Assert.That(final.Status, Is.EqualTo("failed"));

        NotificationItemResponse? found = null;
        for (var i = 0; i < 60 && found is null; i++)
        {
            var page = await _client.GetFromJsonAsync<NotificationPageResponse>(
                "/api/v1/notifications?pageSize=50");
            found = page!.Items.FirstOrDefault(
                n => n.Kind == "run.failed" && n.Link == $"/c/{created.Id}");
            if (found is null)
            {
                await Task.Delay(200);
            }
        }

        Assert.That(found, Is.Not.Null, "run finalizada não gravou linha no feed");
        Assert.That(found!.ReadAt, Is.Null);
    }

    private async Task<ChatRunResponse> AguardarFinalAsync(string chatId, string runId)
    {
        for (var i = 0; i < 400; i++)
        {
            var run = await _client.GetFromJsonAsync<ChatRunResponse>(
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
}

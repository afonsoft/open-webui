using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do auto-sync de memória e da retenção
/// (SPEC-20261010-memory-autosync-retention): serviços invocados via
/// <c>RunOnceAsync</c> — o agendamento em background não é exercitado aqui.
/// </summary>
[TestFixture]
public class MaintenanceServicesTests
{
    private string _root = null!;
    private string _dbPath = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-maint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, "test.db");

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(
            o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IHostEnvironment>(new StubEnvLocal(_root));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IHttpClientFactory>(
            new StubHttpClientFactory(new OllamaStubHandler()));
        services.AddScoped<ConfigService>();
        services.AddScoped<WorkspaceRepoService>();
        services.AddScoped<ProviderService>();
        services.AddScoped<NotificationService>();
        services.AddSingleton<WorktreeService>();
        services.AddScoped<MemorySyncService>();
        services.AddScoped<RetentionService>();
        _provider = services.BuildServiceProvider();

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.SaveChanges();
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private MemorySyncService NewMemSync() =>
        _provider.GetRequiredService<MemorySyncService>();

    private RetentionService NewRetention() =>
        _provider.GetRequiredService<RetentionService>();

    private ConfigService Config() => _provider.GetRequiredService<ConfigService>();

    private async Task SeedChatAsync(string id, string title, long updatedAt)
    {
        await using var db = NewDb();
        var chat = new Chat
        {
            Id = id, UserId = "u1", Title = title,
            ModelsJson = """["fake:1"]""",
            CreatedAt = updatedAt, UpdatedAt = updatedAt,
        };
        chat.Messages.Add(new ChatMessage
        {
            ChatId = id, Role = "user", Content = "o deploy usa fly.io",
            Position = 0, Timestamp = updatedAt,
        });
        chat.Messages.Add(new ChatMessage
        {
            ChatId = id, Role = "assistant", Content = "anotado",
            Position = 1, Timestamp = updatedAt,
        });
        db.Chats.Add(chat);
        await db.SaveChangesAsync();
    }

    // ---------------- Memory sync ----------------

    [Test]
    public async Task MemSync_DestilaChat_GravaMemoriaAuto()
    {
        await Config().SetAsync("connections",
            new ConnectionsConfig(["http://stub.local"], [], []));
        await SeedChatAsync("c1", "deploy notes", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await NewMemSync().RunOnceAsync(default);

        await using var db = NewDb();
        var memory = await db.AgentMemories.SingleAsync(m => m.UserId == "u1");
        Assert.Multiple(() =>
        {
            Assert.That(memory.Title, Is.EqualTo("auto:deploy notes"));
            Assert.That(memory.Scope, Is.EqualTo("global"));
            Assert.That(memory.Content, Does.Contain("fato1"));
        });
    }

    [Test]
    public async Task MemSync_Watermark_NaoReprocessa()
    {
        await Config().SetAsync("connections",
            new ConnectionsConfig(["http://stub.local"], [], []));
        await SeedChatAsync("c1", "t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var sync = NewMemSync();
        await sync.RunOnceAsync(default);
        await sync.RunOnceAsync(default);

        await using var db = NewDb();
        Assert.That(await db.AgentMemories.CountAsync(m => m.UserId == "u1"), Is.EqualTo(1));
    }

    [Test]
    public async Task MemSync_OptOut_NaoSincroniza()
    {
        await Config().SetAsync("u:u1:memsync.enabled", false);
        await SeedChatAsync("c1", "t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await NewMemSync().RunOnceAsync(default);

        await using var db = NewDb();
        Assert.That(await db.AgentMemories.CountAsync(m => m.UserId == "u1"), Is.Zero);
    }

    [Test]
    public async Task MemSync_Poda_Mantem25Auto()
    {
        await Config().SetAsync("connections",
            new ConnectionsConfig(["http://stub.local"], [], []));
        await using (var db = NewDb())
        {
            for (var i = 0; i < 27; i++)
            {
                db.AgentMemories.Add(new AgentMemory
                {
                    UserId = "u1", Title = $"auto:old{i}", Content = "x",
                    UpdatedAt = i,
                });
            }
            await db.SaveChangesAsync();
        }
        await SeedChatAsync("c1", "nova", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await NewMemSync().RunOnceAsync(default);

        await using var check = NewDb();
        var count = await check.AgentMemories
            .CountAsync(m => m.UserId == "u1" && m.Title.StartsWith("auto:"));
        Assert.That(count, Is.EqualTo(25));
    }

    // ---------------- Retention ----------------

    [Test]
    public async Task Retention_ExpurgaRegistrosVelhos_MantemNovos()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var dia = 24 * 3600L;
        await using (var db = NewDb())
        {
            db.Automations.Add(new Automation
            {
                Id = "a1", UserId = "u1", Name = "a", Prompt = "p",
                ModelId = "m", CreatedAt = now,
            });
            db.AutomationRuns.AddRange(
                new AutomationRun
                {
                    AutomationId = "a1", Status = "ok",
                    StartedAt = now - 91 * dia, FinishedAt = now - 91 * dia,
                },
                new AutomationRun
                {
                    AutomationId = "a1", Status = "ok",
                    StartedAt = now - 1 * dia, FinishedAt = now - 1 * dia,
                });
            db.Notifications.AddRange(
                new Notification
                {
                    UserId = "u1", Kind = "k", Title = "velha",
                    CreatedAt = now - 61 * dia,
                },
                new Notification
                {
                    UserId = "u1", Kind = "k", Title = "nova", CreatedAt = now,
                });
            db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
            db.ChatRuns.Add(new ChatRun
            {
                Id = "r1", ChatId = "c1", UserId = "u1", Model = "m",
                Status = ChatRunStatus.Completed,
                CompletedAt = now - 40 * dia, RequestJson = "{}",
            });
            db.ChatRunSteers.AddRange(
                new ChatRunSteer
                {
                    RunId = "r1", ChatId = "c1", Content = "old",
                    Timestamp = now - 31 * dia,
                },
                new ChatRunSteer
                {
                    RunId = "r1", ChatId = "c1", Content = "recente",
                    Timestamp = now - 1 * dia,
                });
            await db.SaveChangesAsync();
        }

        await NewRetention().RunOnceAsync(default);

        await using var check = NewDb();
        Assert.Multiple(() =>
        {
            Assert.That(check.AutomationRuns.Count(), Is.EqualTo(1));
            Assert.That(check.Notifications.Count(), Is.EqualTo(1));
            Assert.That(check.ChatRunSteers.Single().Content, Is.EqualTo("recente"));
        });
    }

    [Test]
    public async Task Retention_Vacuum_RespeitaIntervaloSemanal()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var recent = now - 24 * 3600;
        await Config().SetAsync("sys:vacuum.last", recent);

        await NewRetention().RunOnceAsync(default);

        // Vacuum era recente — timestamp não avança.
        Assert.That(await Config().GetAsync("sys:vacuum.last", 0L), Is.EqualTo(recent));

        // Força last antigo → roda e atualiza.
        await Config().SetAsync("sys:vacuum.last", now - 8L * 24 * 3600);
        await NewRetention().RunOnceAsync(default);
        Assert.That(await Config().GetAsync("sys:vacuum.last", 0L),
            Is.GreaterThanOrEqualTo(now));
    }

    // ---------------- Stubs ----------------

    private sealed class OllamaStubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/api/tags" => """{"models":[{"model":"fake:1","name":"fake:1"}]}""",
                "/api/chat" => """{"message":{"content":"fato1\nfato2"}}""",
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubEnvLocal(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}

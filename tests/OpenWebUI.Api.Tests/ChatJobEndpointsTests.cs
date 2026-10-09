using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes dos endpoints <c>/api/v1/jobs</c> (SPEC-20261007-chat-agent-tools):
/// listagem com filtros (<c>chatId</c>/<c>all</c>), detalhe, saída
/// (<c>output?tail</c>), kill — com isolamento por usuário e 401 sem auth.
/// </summary>
public class ChatJobEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _user = null!;
    private string _workspace = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-jobs-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _workspace = Path.Join(Path.GetTempPath(), $"owui-jobs-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);

        _user = await SignUpAsync("JobOwner", "jobs@jobs.local", "senha123");
        UseToken(_user.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email, string password)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, password));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private ChatJob SeedJob(string id, string chatId, string status,
        string? outputPath = null, int? pid = null)
    {
        using var db = NewDb();
        if (!db.Chats.Any(c => c.Id == chatId))
        {
            db.Chats.Add(new Chat { Id = chatId, UserId = _user.User.Id, Title = "c" });
            db.SaveChanges();
        }

        var job = new ChatJob
        {
            Id = id,
            ChatId = chatId,
            UserId = _user.User.Id,
            Command = "echo x",
            WorkspacePath = _workspace,
            Status = status,
            Pid = pid,
            OutputPath = outputPath,
            StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        db.ChatJobs.Add(job);
        db.SaveChanges();
        return job;
    }

    [Test]
    public async Task Jobs_SemAuth_401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var r = await _client.GetAsync("/api/v1/jobs/");
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Jobs_ListaFiltraDetalhaEDevolveOutput()
    {
        UseToken(_user.Token);
        var outputPath = Path.Join(_workspace, "job1.log");
        await File.WriteAllTextAsync(outputPath, "0123456789ABCDEF");

        SeedJob("job-list-1", "chat-a", ChatJobStatus.Completed, outputPath);
        SeedJob("job-list-2", "chat-b", ChatJobStatus.Running, pid: 999_999_998);

        // Lista por chatId → só os do chat.
        var listA = await _client.GetFromJsonAsync<List<JsonElement>>(
            "/api/v1/jobs/?chatId=chat-a&all=true");
        Assert.That(listA, Has.Count.EqualTo(1));
        Assert.That(listA![0].GetProperty("id").GetString(), Is.EqualTo("job-list-1"));

        // Sem filtro → jobs ativos dos dois chats (running only por padrão).
        var running = await _client.GetFromJsonAsync<List<JsonElement>>("/api/v1/jobs/");
        Assert.That(running!.Any(j => j.GetProperty("id").GetString() == "job-list-2"), Is.True);
        Assert.That(running!.Any(j => j.GetProperty("id").GetString() == "job-list-1"), Is.False);

        // all=true inclui finalizados.
        var all = await _client.GetFromJsonAsync<List<JsonElement>>("/api/v1/jobs/?all=true");
        Assert.That(all!.Any(j => j.GetProperty("id").GetString() == "job-list-1"), Is.True);

        // Detalhe.
        var detail = await _client.GetAsync("/api/v1/jobs/job-list-1");
        Assert.That(detail.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var job = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(job.GetProperty("status").GetString(), Is.EqualTo("completed"));

        // Output com tail.
        var output = await _client.GetAsync("/api/v1/jobs/job-list-1/output?tail=500");
        Assert.That(output.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var payload = await output.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(payload.GetProperty("output").GetString(), Does.Contain("0123456789ABCDEF"));

        // Job inexistente → 404 nos três.
        Assert.That((await _client.GetAsync("/api/v1/jobs/nao-existe")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.GetAsync("/api/v1/jobs/nao-existe/output")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.PostAsync("/api/v1/jobs/nao-existe/kill", null)).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Jobs_KillMataRunningERecusaFinalizado()
    {
        UseToken(_user.Token);
        SeedJob("job-kill-run", "chat-k", ChatJobStatus.Running, pid: 999_999_997);
        SeedJob("job-kill-done", "chat-k", ChatJobStatus.Completed);

        // Job running com pid morto → kill ok (marca killed; processo já morto).
        var kill = await _client.PostAsync("/api/v1/jobs/job-kill-run/kill", null);
        Assert.That(kill.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Finalizado → 404.
        var again = await _client.PostAsync("/api/v1/jobs/job-kill-done/kill", null);
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Jobs_IsolamentoEntreUsuarios()
    {
        UseToken(_user.Token);
        SeedJob("job-privado", "chat-p", ChatJobStatus.Completed);

        var outro = await SignUpAsync("JobOutro", "outro@jobs.local", "senha123");
        UseToken(outro.Token);

        Assert.That((await _client.GetAsync("/api/v1/jobs/job-privado")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.PostAsync("/api/v1/jobs/job-privado/kill", null)).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));

        var list = await _client.GetFromJsonAsync<List<JsonElement>>("/api/v1/jobs/?all=true");
        Assert.That(list!.Any(j => j.GetProperty("id").GetString() == "job-privado"), Is.False);
    }
}

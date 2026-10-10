using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura dos endpoints LSP do IDE (<c>/api/v1/workspace/lsp/*</c>):
/// guard de auth/binding, status por linguagem, doc-sync (open/change/close,
/// jail, arquivo inexistente, degrade sem servidor), diagnostics e hover.
/// Servidores LSP reais não existem no agente — os fluxos exercem o degrade
/// limpo (<c>synced:false</c>/hover null) sem spawn.
/// </summary>
[TestFixture, IsolateEnvironment]
public class LspEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _dataRoot = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-lspep-{Guid.NewGuid():N}.db");
        _dataRoot = Path.Join(Path.GetTempPath(), $"openwebui-lsproot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataRoot);
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("DATA_ROOT", _dataRoot);
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        // Primeiro signup vira admin; libera role "user" pros demais signups.
        var admin = await SignUpAsync("admin@lsp.local");
        UseToken(admin.Token);
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        // DATA_ROOT é process-wide — limpa para não vazar pra outras fixtures.
        Environment.SetEnvironmentVariable("DATA_ROOT", null);
        TestInfra.DeleteDb(_dbPath);
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TestContext.Progress.WriteLine($"cleanup best-effort: {ex.Message}");
        }
    }

    private async Task<AuthResponse> SignUpAsync(string email)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest("User", email, "senha123456"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    /// <summary>Grava o binding do repo direto na config (mesma chave do service).</summary>
    private async Task BindRepoAsync(string userId)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var config = new ConfigService(db, cache);
        await config.SetAsync($"u:{userId}:workspace.repo",
            new WorkspaceRepoBinding("o/b", "main", "repos/o__b"));
    }

    /// <summary>Workdir efetivo sem .git: ResolveWorkdirAsync cai na raiz do workspace.</summary>
    private string WorkdirOf(string userId) =>
        Path.Join(_dataRoot, "workspaces", userId);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    // ---------- guards ----------

    [Test]
    public async Task Status_SemToken_401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var response = await _client.GetAsync("/api/v1/workspace/lsp/status?path=a.py");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Status_SemBinding_404()
    {
        var user = await SignUpAsync($"nb-{Guid.NewGuid():N}@t.local");
        UseToken(user.Token);
        var response = await _client.GetAsync("/api/v1/workspace/lsp/status?path=a.py");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---------- status ----------

    [Test]
    public async Task Status_ExtensaoNaoMapeada_Unmapped()
    {
        var user = await SignUpAsync($"st-{Guid.NewGuid():N}@t.local");
        UseToken(user.Token);
        await BindRepoAsync(user.User.Id);
        var response = await _client.GetAsync("/api/v1/workspace/lsp/status?path=a.xyz");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await ReadJsonAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(json.GetProperty("state").GetString(), Is.EqualTo("unmapped"));
            Assert.That(json.GetProperty("language").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    [Test]
    public async Task Status_PythonMapeado_NotStarted()
    {
        var user = await SignUpAsync($"st2-{Guid.NewGuid():N}@t.local");
        UseToken(user.Token);
        await BindRepoAsync(user.User.Id);
        var response = await _client.GetAsync("/api/v1/workspace/lsp/status?path=a.py");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await ReadJsonAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(json.GetProperty("language").GetString(), Is.EqualTo("python"));
            Assert.That(json.GetProperty("state").GetString(), Is.EqualTo("notstarted"));
        });
    }

    // ---------- doc sync ----------

    [Test]
    public async Task Doc_SemPath_400()
    {
        var user = await SignUpAsync($"doc-{Guid.NewGuid():N}@t.local");
        UseToken(user.Token);
        await BindRepoAsync(user.User.Id);
        var response = await _client.PostAsJsonAsync(
            "/api/v1/workspace/lsp/doc", new { kind = "open" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Doc_PathEscapandoJail_400()
    {
        var user = await SignUpAsync($"doc2-{Guid.NewGuid():N}@t.local");
        UseToken(user.Token);
        await BindRepoAsync(user.User.Id);
        var response = await _client.PostAsJsonAsync(
            "/api/v1/workspace/lsp/doc", new { path = "../fora.py", kind = "open" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Doc_ArquivoInexistenteSemTexto_404()
    {
        var user = await SignUpAsync($"doc3-{Guid.NewGuid():N}@t.local");
        UseToken(user.Token);
        await BindRepoAsync(user.User.Id);
        var response = await _client.PostAsJsonAsync(
            "/api/v1/workspace/lsp/doc", new { path = "a.py", kind = "open" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Doc_OpenSemServidor_SyncedFalse()
    {
        var user = await SignUpAsync($"doc4-{Guid.NewGuid():N}@t.local");
        UseToken(user.Token);
        await BindRepoAsync(user.User.Id);
        var workdir = WorkdirOf(user.User.Id);
        Directory.CreateDirectory(workdir);
        await File.WriteAllTextAsync(Path.Join(workdir, "a.py"), "x=1\n");
        var response = await _client.PostAsJsonAsync(
            "/api/v1/workspace/lsp/doc", new { path = "a.py", kind = "open" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await ReadJsonAsync(response);
        Assert.That(json.GetProperty("synced").GetBoolean(), Is.False,
            "pylsp não instalado → degrade synced:false");
    }

    [Test]
    public async Task Doc_Close_SyncedTrue_EKindInvalido400()
    {
        var user = await SignUpAsync($"doc5-{Guid.NewGuid():N}@t.local");
        UseToken(user.Token);
        await BindRepoAsync(user.User.Id);
        var workdir = WorkdirOf(user.User.Id);
        Directory.CreateDirectory(workdir);
        await File.WriteAllTextAsync(Path.Join(workdir, "a.py"), "x=1\n");

        var close = await _client.PostAsJsonAsync(
            "/api/v1/workspace/lsp/doc", new { path = "a.py", kind = "close" });
        Assert.That(close.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await ReadJsonAsync(close)).GetProperty("synced").GetBoolean(), Is.True);

        var invalid = await _client.PostAsJsonAsync(
            "/api/v1/workspace/lsp/doc", new { path = "a.py", kind = "bogus" });
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Doc_ChangeSemTexto_400()
    {
        var user = await SignUpAsync($"doc6-{Guid.NewGuid():N}@t.local");
        UseToken(user.Token);
        await BindRepoAsync(user.User.Id);
        var workdir = WorkdirOf(user.User.Id);
        Directory.CreateDirectory(workdir);
        await File.WriteAllTextAsync(Path.Join(workdir, "a.py"), "x=1\n");
        var response = await _client.PostAsJsonAsync(
            "/api/v1/workspace/lsp/doc", new { path = "a.py", kind = "change" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---------- diagnostics ----------

    [Test]
    public async Task Diagnostics_SemServidor_ListaVazia()
    {
        var user = await SignUpAsync($"dg-{Guid.NewGuid():N}@t.local");
        UseToken(user.Token);
        await BindRepoAsync(user.User.Id);
        var response = await _client.GetAsync("/api/v1/workspace/lsp/diagnostics");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await ReadJsonAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(json.GetProperty("total").GetInt32(), Is.EqualTo(0));
            Assert.That(json.GetProperty("truncated").GetBoolean(), Is.False);
            Assert.That(json.GetProperty("diagnostics").GetArrayLength(), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Diagnostics_PathForaDoJail_400()
    {
        var user = await SignUpAsync($"dg2-{Guid.NewGuid():N}@t.local");
        UseToken(user.Token);
        await BindRepoAsync(user.User.Id);
        var response = await _client.GetAsync(
            "/api/v1/workspace/lsp/diagnostics?path=../fora.py");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---------- hover ----------

    [Test]
    public async Task Hover_ExtensaoNaoMapeada_Null()
    {
        var user = await SignUpAsync($"hv-{Guid.NewGuid():N}@t.local");
        UseToken(user.Token);
        await BindRepoAsync(user.User.Id);
        var response = await _client.GetAsync(
            "/api/v1/workspace/lsp/hover?path=a.xyz&line=1&col=1");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await ReadJsonAsync(response)).GetProperty("hover").ValueKind,
            Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public async Task Hover_ServidorIndisponivel_Null()
    {
        var user = await SignUpAsync($"hv2-{Guid.NewGuid():N}@t.local");
        UseToken(user.Token);
        await BindRepoAsync(user.User.Id);
        var workdir = WorkdirOf(user.User.Id);
        Directory.CreateDirectory(workdir);
        await File.WriteAllTextAsync(Path.Join(workdir, "a.py"), "x=1\n");
        var response = await _client.GetAsync(
            "/api/v1/workspace/lsp/hover?path=a.py&line=1&col=1");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await ReadJsonAsync(response)).GetProperty("hover").ValueKind,
            Is.EqualTo(JsonValueKind.Null));
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do spawn de terminal local (type "local"): LocalTerminalSpawner
/// sobe o comando configurado como processo filho, aguarda readiness e o
/// proxy reutiliza o pipeline. Nos testes o comando é um http.server
/// neutro — a mesma mecânica do Jupyter real (Jupyter:LocalCommand).
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class LocalTerminalSpawnerTests
{
    private const string SpawnCommand =
        "python3 -m http.server {port} --bind 127.0.0.1 --directory {workdir}";

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static bool HasPython() =>
        Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
            .Any(dir => File.Exists(Path.Join(dir, "python3"))
                     || File.Exists(Path.Join(dir, "python3.exe")));

    private static LocalTerminalSpawner NewSpawner(string command) =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jupyter:LocalCommand"] = command,
                    ["Jupyter:SpawnTimeoutSeconds"] = "10",
                })
                .Build(),
            new StubHttpClientFactory());

    private static TerminalServerConfig LocalServer(string id) =>
        new(id, id, string.Empty, "token", string.Empty, "local");

    [Test]
    public async Task Spawn_ServidorLocal_ResolveUrlEToken_EProxyAlcanca()
    {
        if (!HasPython()) Assert.Ignore("python3 não disponível no ambiente.");
        using var spawner = NewSpawner(SpawnCommand);

        var resolved = await spawner.EnsureStartedAsync(LocalServer("local-py"));
        Assert.Multiple(() =>
        {
            Assert.That(resolved.Url, Does.StartWith("http://127.0.0.1:"));
            Assert.That(resolved.AuthType, Is.EqualTo("token"));
            Assert.That(resolved.Key, Is.Not.Empty, "token gerado para o processo local");
        });

        // O processo spawnado responde qualquer HTTP (404 do http.server prova que subiu).
        using var http = new HttpClient();
        var response = await http.GetAsync($"{resolved.Url}/api/qualquer");
        Assert.That((int)response.StatusCode, Is.InRange(200, 599));

        // Segunda chamada reutiliza o processo em vez de spawnar outro.
        var again = await spawner.EnsureStartedAsync(LocalServer("local-py"));
        Assert.That(again.Url, Is.EqualTo(resolved.Url));

        // Stop + re-resolve respawna em porta nova.
        spawner.Stop("local-py");
        var respawn = await spawner.EnsureStartedAsync(LocalServer("local-py"));
        Assert.That(respawn.Url, Is.Not.EqualTo(resolved.Url));
        var check = await http.GetAsync($"{respawn.Url}/api/x");
        Assert.That((int)check.StatusCode, Is.InRange(200, 599));
    }

    [Test]
    public async Task Spawn_ComandoQueMorre_ErroClaro()
    {
        if (!HasPython()) Assert.Ignore("python3 não disponível no ambiente.");
        using var spawner = NewSpawner("exit 1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => spawner.EnsureStartedAsync(LocalServer("dead")));
        Assert.That(ex!.Message, Does.Contain("dead"));
    }
}

/// <summary>End-to-end: config type "local" sem URL dispara o spawn no proxy.</summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class TerminalLocalEndpointsTests
{
    private const string SpawnCommand =
        "python3 -m http.server {port} --bind 127.0.0.1 --directory {workdir}";

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-termlocal-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("Jupyter__LocalCommand", SpawnCommand);
        Environment.SetEnvironmentVariable("Jupyter__SpawnTimeoutSeconds", "10");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@termlocal.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("Jupyter__LocalCommand", null);
        Environment.SetEnvironmentVariable("Jupyter__SpawnTimeoutSeconds", null);
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
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
    public async Task Config_LocalSemUrl_Aceita_ESpawnNoPrimeiroProxy()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/terminals/config",
            new TerminalServerRequest("Local Py", null, "token", null, "local"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var server = await created.Content.ReadFromJsonAsync<TerminalServerResponse>();
        Assert.That(server!.Type, Is.EqualTo("local"));

        // Primeiro acesso spawna o processo; o http.server devolve 404 próprio
        // ("File not found"), distinguível do 404 de rota sanitizada da API.
        var proxied = await _client.GetAsync("/api/v1/terminals/local-py/api/inexistente");
        Assert.That(proxied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await proxied.Content.ReadAsStringAsync(), Does.Contain("File not found"));

        // Remover a config para o processo (StopLocal no delete).
        var deleted = await _client.DeleteAsync("/api/v1/terminals/config/local-py");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Config_TypeInvalido_AindaRejeita_EUrlExternaSegueObrigatoria()
    {
        var badType = await _client.PostAsJsonAsync("/api/v1/terminals/config",
            new TerminalServerRequest("X", "http://x:1", "token", null, "docker"));
        Assert.That(badType.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var semUrl = await _client.PostAsJsonAsync("/api/v1/terminals/config",
            new TerminalServerRequest("Y", null, "token", null, "jupyter"));
        Assert.That(semUrl.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}

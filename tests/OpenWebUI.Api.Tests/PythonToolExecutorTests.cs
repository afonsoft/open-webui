using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da execução de código Python nas tools (convenção class Tools do
/// upstream): endpoints aceitam Url OU Code, e o PythonToolExecutor roda a
/// fonte em subprocess com resultado via linha marcada no stdout.
/// </summary>
[TestFixture, IsolateEnvironment]
public class PythonToolExecutorTests
{
    private const string Spec =
        "{\"type\":\"function\",\"function\":{\"name\":\"eco\"," +
        "\"parameters\":{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}}}";

    private static bool HasPython() =>
        Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
            .Any(dir => File.Exists(Path.Join(dir, "python3"))
                     || File.Exists(Path.Join(dir, "python3.exe")));

    private static PythonToolExecutor NewExecutor() =>
        new(new ConfigurationBuilder().Build());

    private static Tool CodeTool(string code) => new()
    {
        UserId = "u1",
        Name = "PyTool",
        SpecJson = Spec,
        Code = code,
    };

    [Test]
    public async Task Executa_MetodoSync_RetornaResultado()
    {
        if (!HasPython()) Assert.Ignore("python3 não disponível no ambiente.");
        var tool = CodeTool("""
            class Tools:
                def eco(self, text: str) -> str:
                    return f"eco:{text}"
            """);
        var result = await NewExecutor().ExecuteAsync(tool, "eco", "{\"text\":\"oi\"}");
        Assert.That(result, Is.EqualTo("eco:oi"));
    }

    [Test]
    public async Task Executa_MetodoAsync_EPrintsNaoCorrompemResultado()
    {
        if (!HasPython()) Assert.Ignore("python3 não disponível no ambiente.");
        var tool = CodeTool("""
            class Tools:
                async def eco(self, text: str) -> str:
                    print("log do usuário")
                    print('{"result": "falso"}')
                    return text.upper()
            """);
        var result = await NewExecutor().ExecuteAsync(tool, "eco", "{\"text\":\"abc\"}");
        Assert.That(result, Is.EqualTo("ABC"));
    }

    [Test]
    public async Task Erro_NoMetodo_ExcecaoDoPython_ViraMensagem()
    {
        if (!HasPython()) Assert.Ignore("python3 não disponível no ambiente.");
        var tool = CodeTool("""
            class Tools:
                def eco(self, text: str) -> str:
                    raise ValueError("falhou de propósito")
            """);
        var result = await NewExecutor().ExecuteAsync(tool, "eco", "{\"text\":\"x\"}");
        Assert.That(result, Does.Contain("ValueError").And.Contain("falhou de propósito"));
    }

    [Test]
    public async Task Erro_SemClasseTools_OuFuncaoInexistente()
    {
        if (!HasPython()) Assert.Ignore("python3 não disponível no ambiente.");
        var semClasse = await NewExecutor().ExecuteAsync(
            CodeTool("x = 1"), "eco", "{}");
        Assert.That(semClasse, Does.Contain("NameError"));

        var semMetodo = await NewExecutor().ExecuteAsync(
            CodeTool("class Tools:\n    def outra(self): pass"), "eco", "{}");
        Assert.That(semMetodo, Does.Contain("AttributeError"));
    }

    [Test]
    public async Task ResultadoNaoString_SerializaJson()
    {
        if (!HasPython()) Assert.Ignore("python3 não disponível no ambiente.");
        var tool = CodeTool("""
            class Tools:
                def eco(self, text: str) -> dict:
                    return {"n": 42, "ok": True}
            """);
        var result = await NewExecutor().ExecuteAsync(tool, "eco", "{\"text\":\"x\"}");
        Assert.That(result, Does.Contain("\"n\": 42").Or.Contain("\"n\":42"));
    }
}

/// <summary>Testes de endpoint: aceitação de Code como alternativa a Url.</summary>
[TestFixture, IsolateEnvironment]
public class ToolCodeEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    private const string Spec =
        "{\"type\":\"function\",\"function\":{\"name\":\"eco\",\"parameters\":{\"type\":\"object\"}}}";
    private const string PyCode =
        "class Tools:\n    def eco(self, text: str) -> str:\n        return text";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-toolcode-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@toolcode.local", "senha123");
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
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
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    [Test]
    public async Task Create_ComCodigo_SemUrl_Aceita()
    {
        var user = await SignUpAsync("Py", "py@toolcode.local", "senha123");
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);

        var created = await _client.PostAsJsonAsync("/api/v1/tools/",
            new ToolUpsertRequest("PyTool", null, Spec, null, PyCode));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var tool = (await created.Content.ReadFromJsonAsync<ToolResponse>())!;
        Assert.That(tool.Code, Is.EqualTo(PyCode));

        var updated = await _client.PutAsJsonAsync($"/api/v1/tools/{tool.Id}",
            new ToolUpsertRequest("PyTool", null, Spec, null, PyCode + "\nx = 2"));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var limpa = await _client.PutAsJsonAsync($"/api/v1/tools/{tool.Id}",
            new ToolUpsertRequest("PyTool", null, Spec, null, "", true));
        Assert.That(limpa.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "limpar o código sem URL deve falhar (tool ficaria inexecutável)");
    }

    [Test]
    public async Task Create_SemUrlESemCodigo_OuSemClasseTools_Retorna400()
    {
        var user = await SignUpAsync("Py2", "py2@toolcode.local", "senha123");
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);

        var semNada = await _client.PostAsJsonAsync("/api/v1/tools/",
            new ToolUpsertRequest("X", null, Spec, null));
        Assert.That(semNada.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var semClasse = await _client.PostAsJsonAsync("/api/v1/tools/",
            new ToolUpsertRequest("X", null, Spec, null, "print('oi')"));
        Assert.That(semClasse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}

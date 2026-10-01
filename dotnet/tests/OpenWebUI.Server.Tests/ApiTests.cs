using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Shared.Contracts;

namespace OpenWebUI.Server.Tests;

/// <summary>Testes de integração dos endpoints principais (auth, chats, modelos).</summary>
[TestFixture]
public class ApiTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-tests-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
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

    [Test, Order(1)]
    public async Task Signup_PrimeiroUsuario_ViraAdmin()
    {
        var auth = await SignUpAsync("Admin", "admin@test.local", "senha123");

        Assert.Multiple(() =>
        {
            Assert.That(auth.Token, Is.Not.Empty);
            Assert.That(auth.User.Role, Is.EqualTo("admin"));
            Assert.That(auth.User.Email, Is.EqualTo("admin@test.local"));
        });
    }

    [Test, Order(2)]
    public async Task Signup_EmailDuplicado_RetornaErro()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest("Outro", "admin@test.local", "senha123"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(3)]
    public async Task Signup_SegundoUsuario_ViraUser()
    {
        var auth = await SignUpAsync("User", "user@test.local", "senha123");

        Assert.That(auth.User.Role, Is.EqualTo("user"));
    }

    [Test, Order(4)]
    public async Task Signin_SenhaErrada_RetornaErro()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signin", new SignInRequest("admin@test.local", "errada"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(5)]
    public async Task Signin_CredenciaisValidas_RetornaToken()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signin", new SignInRequest("admin@test.local", "senha123"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.That(auth.Token, Is.Not.Empty);
    }

    [Test, Order(6)]
    public async Task Me_ComToken_RetornaUsuario()
    {
        var auth = await SignUpAsync("Me", "me@test.local", "senha123");
        UseToken(auth.Token);

        var user = await _client.GetFromJsonAsync<UserResponse>("/api/v1/auths/");

        Assert.That(user?.Email, Is.EqualTo("me@test.local"));
    }

    [Test, Order(7)]
    public async Task Chats_SemToken_Retorna401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var response = await _client.GetAsync("/api/v1/chats/");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test, Order(8)]
    public async Task Chats_CicloCompleto_CriaListaAtualizaRemove()
    {
        var auth = await SignUpAsync("Chat", "chat@test.local", "senha123");
        UseToken(auth.Token);

        var messages = new List<ChatMessageModel>
        {
            new("m1", "user", "Olá", null, 100),
            new("m2", "assistant", "Olá! Como posso ajudar?", "llama3", 101),
        };

        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Meu chat", ["llama3"], messages));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(chat.Title, Is.EqualTo("Meu chat"));
            Assert.That(chat.Messages, Has.Count.EqualTo(2));
        });

        var list = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>("/api/v1/chats/");
        Assert.That(list, Has.Count.EqualTo(1));

        var updated = await _client.PostAsJsonAsync($"/api/v1/chats/{chat.Id}",
            new ChatUpsertRequest("Renomeado", ["llama3"], messages));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var updatedChat = (await updated.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.That(updatedChat.Title, Is.EqualTo("Renomeado"));

        var deleted = await _client.DeleteAsync($"/api/v1/chats/{chat.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var listAfter = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>("/api/v1/chats/");
        Assert.That(listAfter, Is.Empty);
    }

    [Test, Order(9)]
    public async Task Chats_Busca_FiltraPorTitulo()
    {
        var auth = await SignUpAsync("Busca", "busca@test.local", "senha123");
        UseToken(auth.Token);
        var msgs = new List<ChatMessageModel> { new("m1", "user", "oi", null, 1) };

        await _client.PostAsJsonAsync("/api/v1/chats/", new ChatUpsertRequest("Python ajuda", [], msgs));
        await _client.PostAsJsonAsync("/api/v1/chats/", new ChatUpsertRequest("Receita bolo", [], msgs));

        var filtered = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>(
            "/api/v1/chats/?query=python");

        Assert.That(filtered, Has.Count.EqualTo(1));
        Assert.That(filtered![0].Title, Is.EqualTo("Python ajuda"));
    }

    [Test, Order(10)]
    public async Task Models_ComToken_RetornaListaVaziaSemProvedores()
    {
        var auth = await SignUpAsync("Models", "models@test.local", "senha123");
        UseToken(auth.Token);

        var result = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Data, Is.Not.Null);
    }

    [Test, Order(11)]
    public async Task Version_SemAuth_RetornaVersao()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var result = await _client.GetFromJsonAsync<VersionResponse>("/api/version");

        Assert.That(result?.Version, Does.Contain("dotnet"));
    }

    [Test, Order(12)]
    public async Task Connections_UsuarioComum_Retorna403()
    {
        var auth = await SignUpAsync("Comum", "comum@test.local", "senha123");
        UseToken(auth.Token);

        var response = await _client.GetAsync("/api/v1/configs/connections");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test, Order(13)]
    public async Task Connections_Admin_LeEAtualiza()
    {
        var signin = await _client.PostAsJsonAsync(
            "/api/v1/auths/signin", new SignInRequest("admin@test.local", "senha123"));
        var auth = (await signin.Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(auth.Token);

        var get = await _client.GetFromJsonAsync<ConnectionsConfigResponse>(
            "/api/v1/configs/connections");
        Assert.That(get, Is.Not.Null);

        var update = await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig(
                ["http://localhost:11434"],
                ["https://api.openai.com/v1"],
                ["sk-test"]));
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var after = await _client.GetFromJsonAsync<ConnectionsConfigResponse>(
            "/api/v1/configs/connections");
        Assert.Multiple(() =>
        {
            Assert.That(after!.OpenAiBaseUrls, Does.Contain("https://api.openai.com/v1"));
            Assert.That(after.OpenAiKeyConfigured[0], Is.True);
        });
    }
}

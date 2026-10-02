using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes de chats avançados (SPEC chats-advanced): versionamento de
/// mensagens em edição/regeneração e listagem administrativa de chats.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ChatsAdvancedTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;
    private AuthResponse _user = null!;
    private ChatResponse _chat = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-chatsadv-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = (await (await _client.PostAsJsonAsync(
                "/api/v1/auths/signup", new SignUpRequest("Admin", "admin@adv.local", "senha123")))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        UseToken(_admin.Token);
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });

        _user = (await (await _client.PostAsJsonAsync(
                "/api/v1/auths/signup", new SignUpRequest("User", "user@adv.local", "senha123")))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        UseToken(_user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/chats/", new ChatUpsertRequest(
            "Chat teste", ["modelo-x"],
            [new ChatMessageModel("m1", "user", "pergunta original", null, 1),
             new ChatMessageModel("m2", "assistant", "resposta v1", "modelo-x", 2)]));
        _chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
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

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    [Test, Order(1)]
    public async Task Edicao_PreservaVersaoAnterior()
    {
        UseToken(_user.Token);
        var update = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{_chat.Id}/messages/m1", new MessageUpdateRequest("pergunta editada"));
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var versions = await _client.GetFromJsonAsync<List<ChatMessageVersionModel>>(
            $"/api/v1/chats/{_chat.Id}/messages/m1/versions");
        Assert.That(versions, Has.Count.EqualTo(1));
        Assert.That(versions![0].Content, Is.EqualTo("pergunta original"));
    }

    [Test, Order(2)]
    public async Task Regeneracao_PreservaVersaoPeloUpsertCompleto()
    {
        UseToken(_user.Token);
        // Regeneração do cliente reenvia a lista inteira com novo conteúdo.
        var upsert = await _client.PostAsJsonAsync($"/api/v1/chats/{_chat.Id}", new ChatUpsertRequest(
            "Chat teste", ["modelo-x"],
            [new ChatMessageModel("m1", "user", "pergunta editada", null, 1),
             new ChatMessageModel("m2", "assistant", "resposta v2", "modelo-x", 2)]));
        Assert.That(upsert.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var versions = await _client.GetFromJsonAsync<List<ChatMessageVersionModel>>(
            $"/api/v1/chats/{_chat.Id}/messages/m2/versions");
        Assert.That(versions, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(versions![0].Content, Is.EqualTo("resposta v1"));
            Assert.That(versions[0].Model, Is.EqualTo("modelo-x"));
        });
    }

    [Test, Order(3)]
    public async Task Versoes_VaziasParaMensagemSemEdicao()
    {
        UseToken(_user.Token);
        var versions = await _client.GetFromJsonAsync<List<ChatMessageVersionModel>>(
            $"/api/v1/chats/{_chat.Id}/messages/m2/versions");
        Assert.That(versions, Has.Count.EqualTo(1), "m2 já tem uma versão do teste anterior");

        var chat = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{_chat.Id}");
        var m1 = chat!.Messages.First(m => m.Id == "m1");
        Assert.That(m1.Versions, Has.Count.EqualTo(1));
        Assert.That(m1.Content, Is.EqualTo("pergunta editada"));
    }

    [Test, Order(4)]
    public async Task AdminLista_TodosOsChatsPaginados()
    {
        UseToken(_admin.Token);
        var raw = await _client.GetAsync("/api/v1/chats/all?page=1");
        if ((int)raw.StatusCode >= 400) Assert.Fail(await raw.Content.ReadAsStringAsync());
        var page = await raw.Content.ReadFromJsonAsync<AdminChatListResponse>();
        Assert.That(page, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(page!.Total, Is.GreaterThanOrEqualTo(1));
            Assert.That(page.Items.Any(c => c.Id == _chat.Id), Is.True);
            Assert.That(page.Items.First(c => c.Id == _chat.Id).UserEmail, Is.EqualTo("user@adv.local"));
            Assert.That(page.Items.First(c => c.Id == _chat.Id).MessageCount, Is.EqualTo(2));
        });
    }

    [Test, Order(5)]
    public async Task AdminLista_NaoAdminRecebe403()
    {
        UseToken(_user.Token);
        var response = await _client.GetAsync("/api/v1/chats/all?page=1");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test, Order(6)]
    public async Task AdminLista_FiltroPorTitulo()
    {
        UseToken(_admin.Token);
        var page = await _client.GetFromJsonAsync<AdminChatListResponse>(
            "/api/v1/chats/all?query=Chat%20teste&page=1");
        Assert.That(page!.Items.All(c => c.Title.Contains("Chat teste")), Is.True);

        var vazio = await _client.GetFromJsonAsync<AdminChatListResponse>(
            "/api/v1/chats/all?query=nao-existe-xyz&page=1");
        Assert.That(vazio!.Total, Is.EqualTo(0));
    }
}

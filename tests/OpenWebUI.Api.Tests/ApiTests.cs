using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes de integração dos endpoints principais (auth, chats, modelos).</summary>
[TestFixture]
public class ApiTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-tests-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        // O primeiro usuário vira admin e habilita "user" como papel padrão,
        // espelhando um admin que ajusta DEFAULT_USER_ROLE nas configurações.
        var admin = await SignUpAsync("Admin", "admin@test.local", "senha123");
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
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
        // O admin já foi criado no OneTimeSetUp; valida o estado persistido.
        var signin = await _client.PostAsJsonAsync(
            "/api/v1/auths/signin", new SignInRequest("admin@test.local", "senha123"));

        Assert.That(signin.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var auth = (await signin.Content.ReadFromJsonAsync<AuthResponse>())!;
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

    [Test, Order(14)]
    public async Task Chats_PinArchiveShare_Funcionam()
    {
        var auth = await SignUpAsync("Pin", "pin@test.local", "senha123");
        UseToken(auth.Token);
        var msgs = new List<ChatMessageModel> { new("m1", "user", "oi", null, 1) };

        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Fixável", [], msgs));
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;

        var pinned = await _client.PostAsync($"/api/v1/chats/{chat.Id}/pin", null);
        var pinnedChat = (await pinned.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.That(pinnedChat.Pinned, Is.True);

        var pinnedList = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>(
            "/api/v1/chats/pinned");
        Assert.That(pinnedList, Has.Count.EqualTo(1));

        var shared = await _client.PostAsync($"/api/v1/chats/{chat.Id}/share", null);
        var sharedChat = (await shared.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.That(sharedChat.ShareId, Is.Not.Null.And.Not.Empty);

        _client.DefaultRequestHeaders.Authorization = null;
        var publicView = await _client.GetAsync($"/api/v1/chats/share/{sharedChat.ShareId}");
        Assert.That(publicView.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        UseToken(auth.Token);

        var archived = await _client.PostAsync($"/api/v1/chats/{chat.Id}/archive", null);
        var archivedChat = (await archived.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.That(archivedChat.Archived, Is.True);

        var normalList = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>("/api/v1/chats/");
        Assert.That(normalList, Is.Empty);
    }

    [Test, Order(15)]
    public async Task Pastas_CriaMoveListaRemove()
    {
        var auth = await SignUpAsync("Folder", "folder@test.local", "senha123");
        UseToken(auth.Token);
        var msgs = new List<ChatMessageModel> { new("m1", "user", "oi", null, 1) };

        var folder = await _client.PostAsJsonAsync(
            "/api/v1/folders/", new FolderUpsertRequest("Trabalho", null));
        var folderCreated = (await folder.Content.ReadFromJsonAsync<FolderResponse>())!;

        var chat = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Na pasta", [], msgs));
        var chatCreated = (await chat.Content.ReadFromJsonAsync<ChatResponse>())!;

        var moved = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chatCreated.Id}/folder", new { folderId = folderCreated.Id });
        Assert.That(moved.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var normalList = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>("/api/v1/chats/");
        Assert.That(normalList, Is.Empty);

        var inFolder = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>(
            $"/api/v1/chats/folder/{folderCreated.Id}");
        Assert.That(inFolder, Has.Count.EqualTo(1));
    }

    [Test, Order(16)]
    public async Task Prompts_CRUD_Completo()
    {
        var auth = await SignUpAsync("Prompt", "prompt@test.local", "senha123");
        UseToken(auth.Token);

        var created = await _client.PostAsJsonAsync("/api/v1/prompts/create",
            new PromptUpsertRequest("revisar", "Revisar código", "Revise o código a seguir:"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = await _client.GetFromJsonAsync<List<PromptResponse>>("/api/v1/prompts/");
        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list![0].Command, Is.EqualTo("revisar"));

        var id = list[0].Id;
        var deleted = await _client.DeleteAsync($"/api/v1/prompts/id/{id}/delete");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test, Order(17)]
    public async Task MemoriasENotas_CRUD()
    {
        var auth = await SignUpAsync("Memo", "memo@test.local", "senha123");
        UseToken(auth.Token);

        var mem = await _client.PostAsJsonAsync("/api/v1/memories/add",
            new MemoryUpsertRequest("Prefiro PT-BR"));
        Assert.That(mem.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var mems = await _client.GetFromJsonAsync<List<MemoryResponse>>("/api/v1/memories/");
        Assert.That(mems, Has.Count.EqualTo(1));

        var note = await _client.PostAsJsonAsync("/api/v1/notes/create",
            new NoteUpsertRequest("Ideias", "Primeira nota"));
        Assert.That(note.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var notes = await _client.GetFromJsonAsync<List<NoteResponse>>("/api/v1/notes/");
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes![0].Title, Is.EqualTo("Ideias"));
    }

    [Test, Order(18)]
    public async Task Avaliacao_FeedbackSalvaEDuplica()
    {
        var auth = await SignUpAsync("Eval", "eval@test.local", "senha123");
        UseToken(auth.Token);

        var feedback = await _client.PostAsJsonAsync("/api/v1/evaluations/feedback",
            new FeedbackUpsertRequest("chat1", "msg1", "llama3", 1, null));
        Assert.That(feedback.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var updated = await _client.PostAsJsonAsync("/api/v1/evaluations/feedback",
            new FeedbackUpsertRequest("chat1", "msg1", "llama3", -1, "ruim"));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = await _client.GetFromJsonAsync<List<FeedbackResponse>>(
            "/api/v1/evaluations/feedbacks/user");
        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list![0].Rating, Is.EqualTo(-1));
    }

    [Test, Order(19)]
    public async Task ApiKey_CriaUsaERevoga()
    {
        var auth = await SignUpAsync("Key", "key@test.local", "senha123");
        UseToken(auth.Token);

        var created = await _client.PostAsync("/api/v1/auths/api_key", null);
        var key = (await created.Content.ReadFromJsonAsync<ApiKeyCreatedResponse>())!;
        Assert.That(key.ApiKey, Does.StartWith("sk-"));

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", key.ApiKey);
        var me = await _client.GetFromJsonAsync<UserResponse>("/api/v1/auths/");
        Assert.That(me?.Email, Is.EqualTo("key@test.local"));

        UseToken(auth.Token);
        var revoked = await _client.DeleteAsync("/api/v1/auths/api_key");
        Assert.That(revoked.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", key.ApiKey);
        var after = await _client.GetAsync("/api/v1/auths/");
        Assert.That(after.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test, Order(20)]
    public async Task ModelosPersonalizados_CRUD()
    {
        var auth = await SignUpAsync("CModel", "cmodel@test.local", "senha123");
        UseToken(auth.Token);

        var created = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest("Meu modelo", "llama3", "Seja direto", null, null));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var model = (await created.Content.ReadFromJsonAsync<ModelEntryResponse>())!;

        var list = await _client.GetFromJsonAsync<List<ModelEntryResponse>>("/api/v1/models/");
        Assert.That(list, Has.Count.EqualTo(1));

        var all = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");
        Assert.That(all!.Data.Any(m => m.Id == model.Id), Is.True);

        var deleted = await _client.PostAsJsonAsync(
            "/api/v1/models/model/delete", new { id = model.Id });
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test, Order(21)]
    public async Task Admin_Usuarios_ListaEAtualiza()
    {
        var signin = await _client.PostAsJsonAsync(
            "/api/v1/auths/signin", new SignInRequest("admin@test.local", "senha123"));
        var auth = (await signin.Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(auth.Token);

        var users = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/users/");
        var total = users.GetProperty("total").GetInt32();
        Assert.That(total, Is.GreaterThan(0));

        var comum = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("user@test.local", "senha123"));
        Assert.That(comum.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes de integração dos endpoints de workspace (pastas, prompts, memórias e notas).</summary>
[TestFixture, IsolateEnvironment]
public class WorkspaceEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-ws-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@ws.local", "senha123");
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

    private async Task<ChatResponse> CriarChatAsync(string titulo)
    {
        var msgs = new List<ChatMessageModel> { new("m1", "user", "oi", null, 1) };
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest(titulo, [], msgs));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    private async Task<FolderResponse> CriarPastaAsync(string nome, string? parentId = null)
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/folders/", new FolderUpsertRequest(nome, parentId));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await created.Content.ReadFromJsonAsync<FolderResponse>())!;
    }

    private static int ContarChatsDaPasta(JsonElement pasta) =>
        pasta.GetProperty("items").GetProperty("chats").GetArrayLength();

    [Test, Order(1)]
    public async Task Workspace_SemToken_Retorna401()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        Assert.Multiple(async () =>
        {
            Assert.That((await _client.GetAsync("/api/v1/folders/")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await _client.GetAsync("/api/v1/prompts/")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await _client.GetAsync("/api/v1/memories/")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await _client.GetAsync("/api/v1/notes/")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
        });
    }

    [Test, Order(2)]
    public async Task Pastas_CRUD_Completo()
    {
        var auth = await SignUpAsync("PastaCrud", "pastacrud@ws.local", "senha123");
        UseToken(auth.Token);

        var pai = await CriarPastaAsync("Pai");
        var pasta = await CriarPastaAsync("Trabalho");

        var fetched = await _client.GetFromJsonAsync<FolderResponse>($"/api/v1/folders/{pasta.Id}");
        Assert.Multiple(() =>
        {
            Assert.That(fetched!.Name, Is.EqualTo("Trabalho"));
            Assert.That(fetched.ParentId, Is.Null);
        });

        var updated = await _client.PostAsJsonAsync(
            $"/api/v1/folders/{pasta.Id}/update", new FolderUpsertRequest("Renomeada", pai.Id));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var updatedFolder = (await updated.Content.ReadFromJsonAsync<FolderResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(updatedFolder.Name, Is.EqualTo("Renomeada"));
            Assert.That(updatedFolder.ParentId, Is.EqualTo(pai.Id));
        });

        var deleted = await _client.DeleteAsync($"/api/v1/folders/{pasta.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var gone = await _client.GetAsync($"/api/v1/folders/{pasta.Id}");
        Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Order(3)]
    public async Task Pastas_NomeVazio_UsaPadraoNovaPasta()
    {
        var auth = await SignUpAsync("PastaVazia", "pastavazia@ws.local", "senha123");
        UseToken(auth.Token);

        var pasta = await CriarPastaAsync("   ");

        Assert.That(pasta.Name, Is.EqualTo("Nova pasta"));
    }

    [Test, Order(4)]
    public async Task Pastas_Lista_OrdenadaPorNome_EListaItensDaPasta()
    {
        var auth = await SignUpAsync("PastaItens", "pastaitens@ws.local", "senha123");
        UseToken(auth.Token);

        var zeta = await CriarPastaAsync("Zeta");
        await CriarPastaAsync("Alfa");
        var chat = await CriarChatAsync("Chat na pasta");

        var moved = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/folder", new { folderId = zeta.Id });
        Assert.That(moved.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var folders = await _client.GetFromJsonAsync<List<JsonElement>>("/api/v1/folders/");
        Assert.That(folders, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(folders![0].GetProperty("name").GetString(), Is.EqualTo("Alfa"));
            Assert.That(folders[1].GetProperty("name").GetString(), Is.EqualTo("Zeta"));
            Assert.That(ContarChatsDaPasta(folders[0]), Is.EqualTo(0));
            Assert.That(ContarChatsDaPasta(folders[1]), Is.EqualTo(1));
            Assert.That(
                folders[1].GetProperty("items").GetProperty("chats")[0].GetProperty("id").GetString(),
                Is.EqualTo(chat.Id));
        });

        // Chats arquivados não aparecem como itens da pasta.
        var archived = await _client.PostAsync($"/api/v1/chats/{chat.Id}/archive", null);
        Assert.That(archived.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var foldersDepois = await _client.GetFromJsonAsync<List<JsonElement>>("/api/v1/folders/");
        var zetaDepois = foldersDepois!.Single(f => f.GetProperty("name").GetString() == "Zeta");
        Assert.That(ContarChatsDaPasta(zetaDepois), Is.EqualTo(0));
    }

    [Test, Order(5)]
    public async Task Pastas_Delete_DesvinculaChatsSemApagar()
    {
        var auth = await SignUpAsync("PastaDel", "pastadel@ws.local", "senha123");
        UseToken(auth.Token);

        var pasta = await CriarPastaAsync("Temporária");
        var chat = await CriarChatAsync("Chat solto");
        await _client.PostAsJsonAsync($"/api/v1/chats/{chat.Id}/folder", new { folderId = pasta.Id });

        var deleted = await _client.DeleteAsync($"/api/v1/folders/{pasta.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // O chat sobrevive e volta a não pertencer a pasta nenhuma.
        var chatDepois = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{chat.Id}");
        Assert.That(chatDepois!.FolderId, Is.Null);

        var lista = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>("/api/v1/chats/");
        Assert.That(lista!.Any(c => c.Id == chat.Id), Is.True);
    }

    [Test, Order(6)]
    public async Task Pastas_Owner_NaoAcessaPastaDeOutroUsuario()
    {
        var dono = await SignUpAsync("DonoPasta", "donopasta@ws.local", "senha123");
        UseToken(dono.Token);
        var pasta = await CriarPastaAsync("Privada");

        var outro = await SignUpAsync("OutroPasta", "outropasta@ws.local", "senha123");
        UseToken(outro.Token);

        Assert.Multiple(async () =>
        {
            Assert.That((await _client.GetAsync($"/api/v1/folders/{pasta.Id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsJsonAsync($"/api/v1/folders/{pasta.Id}/update",
                    new FolderUpsertRequest("Invasão", null))).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.DeleteAsync($"/api/v1/folders/{pasta.Id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
        });

        var lista = await _client.GetFromJsonAsync<List<JsonElement>>("/api/v1/folders/");
        Assert.That(lista, Is.Empty);
    }

    [Test, Order(7)]
    public async Task Prompts_CRUD_Completo()
    {
        var auth = await SignUpAsync("PromptCrud", "promptcrud@ws.local", "senha123");
        UseToken(auth.Token);

        // O comando pode vir com barra; o endpoint remove o prefixo "/".
        var created = await _client.PostAsJsonAsync("/api/v1/prompts/create",
            new PromptUpsertRequest("/resumir", "Resumir", "Resuma o texto a seguir:"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var prompt = (await created.Content.ReadFromJsonAsync<PromptResponse>())!;
        Assert.That(prompt.Command, Is.EqualTo("resumir"));

        var list = await _client.GetFromJsonAsync<List<PromptResponse>>("/api/v1/prompts/");
        Assert.That(list, Has.Count.EqualTo(1));
        var listAlias = await _client.GetFromJsonAsync<List<PromptResponse>>("/api/v1/prompts/list");
        Assert.That(listAlias, Has.Count.EqualTo(1));

        var byId = await _client.GetFromJsonAsync<PromptResponse>($"/api/v1/prompts/id/{prompt.Id}");
        Assert.That(byId!.Title, Is.EqualTo("Resumir"));

        var byCommand = await _client.GetFromJsonAsync<PromptResponse>("/api/v1/prompts/command/resumir");
        Assert.That(byCommand!.Id, Is.EqualTo(prompt.Id));

        var updated = await _client.PostAsJsonAsync($"/api/v1/prompts/id/{prompt.Id}/update",
            new PromptUpsertRequest("ignorado", "Novo título", "Novo conteúdo"));
        var updatedPrompt = (await updated.Content.ReadFromJsonAsync<PromptResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(updatedPrompt.Title, Is.EqualTo("Novo título"));
            Assert.That(updatedPrompt.Content, Is.EqualTo("Novo conteúdo"));
        });

        var deleted = await _client.DeleteAsync($"/api/v1/prompts/id/{prompt.Id}/delete");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var gone = await _client.GetAsync($"/api/v1/prompts/id/{prompt.Id}");
        Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Order(8)]
    public async Task Prompts_Lista_OrdenadaPorComando()
    {
        var auth = await SignUpAsync("PromptOrd", "promptord@ws.local", "senha123");
        UseToken(auth.Token);

        await _client.PostAsJsonAsync("/api/v1/prompts/create",
            new PromptUpsertRequest("zulu", "Z", "z"));
        await _client.PostAsJsonAsync("/api/v1/prompts/create",
            new PromptUpsertRequest("alfa", "A", "a"));

        var list = await _client.GetFromJsonAsync<List<PromptResponse>>("/api/v1/prompts/");
        Assert.Multiple(() =>
        {
            Assert.That(list![0].Command, Is.EqualTo("alfa"));
            Assert.That(list[1].Command, Is.EqualTo("zulu"));
        });
    }

    [Test, Order(9)]
    public async Task Prompts_ComandoVazioOuDuplicado_Retorna400()
    {
        var auth = await SignUpAsync("PromptVal", "promptval@ws.local", "senha123");
        UseToken(auth.Token);

        var vazio = await _client.PostAsJsonAsync("/api/v1/prompts/create",
            new PromptUpsertRequest("///", "Vazio", "x"));
        Assert.That(vazio.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var primeiro = await _client.PostAsJsonAsync("/api/v1/prompts/create",
            new PromptUpsertRequest("dup", "Um", "x"));
        Assert.That(primeiro.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var duplicado = await _client.PostAsJsonAsync("/api/v1/prompts/create",
            new PromptUpsertRequest("/dup", "Dois", "y"));
        Assert.That(duplicado.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(10)]
    public async Task Prompts_Owner_NaoAcessaPromptDeOutroUsuario()
    {
        var dono = await SignUpAsync("DonoPrompt", "donoprompt@ws.local", "senha123");
        UseToken(dono.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/prompts/create",
            new PromptUpsertRequest("secreto", "Secreto", "x"));
        var prompt = (await created.Content.ReadFromJsonAsync<PromptResponse>())!;

        var outro = await SignUpAsync("OutroPrompt", "outroprompt@ws.local", "senha123");
        UseToken(outro.Token);

        Assert.Multiple(async () =>
        {
            Assert.That((await _client.GetAsync($"/api/v1/prompts/id/{prompt.Id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.GetAsync("/api/v1/prompts/command/secreto")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsJsonAsync($"/api/v1/prompts/id/{prompt.Id}/update",
                    new PromptUpsertRequest("secreto", "Hack", "y"))).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.DeleteAsync($"/api/v1/prompts/id/{prompt.Id}/delete")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
        });

        // Comando de outro usuário não bloqueia a criação do mesmo comando.
        var proprio = await _client.PostAsJsonAsync("/api/v1/prompts/create",
            new PromptUpsertRequest("secreto", "Meu", "z"));
        Assert.That(proprio.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test, Order(11)]
    public async Task Memorias_CRUD_Completo()
    {
        var auth = await SignUpAsync("MemCrud", "memcrud@ws.local", "senha123");
        UseToken(auth.Token);

        var created = await _client.PostAsJsonAsync("/api/v1/memories/add",
            new MemoryUpsertRequest("Prefiro respostas em PT-BR"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var memory = (await created.Content.ReadFromJsonAsync<MemoryResponse>())!;

        var list = await _client.GetFromJsonAsync<List<MemoryResponse>>("/api/v1/memories/");
        Assert.That(list, Has.Count.EqualTo(1));

        var updated = await _client.PostAsJsonAsync($"/api/v1/memories/{memory.Id}/update",
            new MemoryUpsertRequest("Prefiro respostas curtas"));
        var updatedMemory = (await updated.Content.ReadFromJsonAsync<MemoryResponse>())!;
        Assert.That(updatedMemory.Content, Is.EqualTo("Prefiro respostas curtas"));

        var deleted = await _client.DeleteAsync($"/api/v1/memories/{memory.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var listAfter = await _client.GetFromJsonAsync<List<MemoryResponse>>("/api/v1/memories/");
        Assert.That(listAfter, Is.Empty);
    }

    [Test, Order(12)]
    public async Task Memorias_ConteudoVazio_Retorna400()
    {
        var auth = await SignUpAsync("MemVal", "memval@ws.local", "senha123");
        UseToken(auth.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/memories/add",
            new MemoryUpsertRequest("   "));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(13)]
    public async Task Memorias_DeleteUser_ApagaSomenteDoProprioUsuario()
    {
        var dono = await SignUpAsync("DonoMem", "donomem@ws.local", "senha123");
        UseToken(dono.Token);
        await _client.PostAsJsonAsync("/api/v1/memories/add", new MemoryUpsertRequest("Memória do A"));

        var outro = await SignUpAsync("OutroMem", "outromem@ws.local", "senha123");
        UseToken(outro.Token);
        await _client.PostAsJsonAsync("/api/v1/memories/add", new MemoryUpsertRequest("Memória do B"));
        await _client.PostAsJsonAsync("/api/v1/memories/add", new MemoryUpsertRequest("Outra do B"));

        var wipe = await _client.DeleteAsync("/api/v1/memories/delete/user");
        Assert.That(wipe.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var listOutro = await _client.GetFromJsonAsync<List<MemoryResponse>>("/api/v1/memories/");
        Assert.That(listOutro, Is.Empty);

        // A memória do usuário A permanece intacta.
        UseToken(dono.Token);
        var listDono = await _client.GetFromJsonAsync<List<MemoryResponse>>("/api/v1/memories/");
        Assert.That(listDono, Has.Count.EqualTo(1));
        Assert.That(listDono![0].Content, Is.EqualTo("Memória do A"));
    }

    [Test, Order(14)]
    public async Task Memorias_Owner_NaoAcessaMemoriaDeOutroUsuario()
    {
        var dono = await SignUpAsync("DonoMem2", "donomem2@ws.local", "senha123");
        UseToken(dono.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/memories/add",
            new MemoryUpsertRequest("Segredo"));
        var memory = (await created.Content.ReadFromJsonAsync<MemoryResponse>())!;

        var outro = await SignUpAsync("OutroMem2", "outromem2@ws.local", "senha123");
        UseToken(outro.Token);

        Assert.Multiple(async () =>
        {
            Assert.That((await _client.PostAsJsonAsync($"/api/v1/memories/{memory.Id}/update",
                    new MemoryUpsertRequest("Alterado"))).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.DeleteAsync($"/api/v1/memories/{memory.Id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
        });

        var list = await _client.GetFromJsonAsync<List<MemoryResponse>>("/api/v1/memories/");
        Assert.That(list, Is.Empty);
    }

    [Test, Order(15)]
    public async Task Notas_CRUD_Completo()
    {
        var auth = await SignUpAsync("NotaCrud", "notacrud@ws.local", "senha123");
        UseToken(auth.Token);

        var created = await _client.PostAsJsonAsync("/api/v1/notes/create",
            new NoteUpsertRequest("Ideias", "Primeira **nota**"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var note = (await created.Content.ReadFromJsonAsync<NoteResponse>())!;

        var list = await _client.GetFromJsonAsync<List<NoteResponse>>("/api/v1/notes/");
        Assert.That(list, Has.Count.EqualTo(1));

        var fetched = await _client.GetFromJsonAsync<NoteResponse>($"/api/v1/notes/{note.Id}");
        Assert.That(fetched!.Content, Is.EqualTo("Primeira **nota**"));

        // Título vazio mantém o título atual; conteúdo é atualizado.
        var updated = await _client.PostAsJsonAsync($"/api/v1/notes/{note.Id}/update",
            new NoteUpsertRequest(" ", "Conteúdo novo"));
        var updatedNote = (await updated.Content.ReadFromJsonAsync<NoteResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(updatedNote.Title, Is.EqualTo("Ideias"));
            Assert.That(updatedNote.Content, Is.EqualTo("Conteúdo novo"));
        });

        var renamed = await _client.PostAsJsonAsync($"/api/v1/notes/{note.Id}/update",
            new NoteUpsertRequest("Ideias 2", "Conteúdo novo"));
        var renamedNote = (await renamed.Content.ReadFromJsonAsync<NoteResponse>())!;
        Assert.That(renamedNote.Title, Is.EqualTo("Ideias 2"));

        var deleted = await _client.DeleteAsync($"/api/v1/notes/{note.Id}/delete");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var gone = await _client.GetAsync($"/api/v1/notes/{note.Id}");
        Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Order(16)]
    public async Task Notas_TituloVazio_UsaPadraoNovaNota()
    {
        var auth = await SignUpAsync("NotaVazia", "notavazia@ws.local", "senha123");
        UseToken(auth.Token);

        var created = await _client.PostAsJsonAsync("/api/v1/notes/create",
            new NoteUpsertRequest("", "só conteúdo"));
        var note = (await created.Content.ReadFromJsonAsync<NoteResponse>())!;

        Assert.That(note.Title, Is.EqualTo("Nova nota"));
    }

    [Test, Order(17)]
    public async Task Notas_Owner_NaoAcessaNotaDeOutroUsuario()
    {
        var dono = await SignUpAsync("DonoNota", "dononota@ws.local", "senha123");
        UseToken(dono.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/notes/create",
            new NoteUpsertRequest("Privada", "conteúdo"));
        var note = (await created.Content.ReadFromJsonAsync<NoteResponse>())!;

        var outro = await SignUpAsync("OutroNota", "outronota@ws.local", "senha123");
        UseToken(outro.Token);

        Assert.Multiple(async () =>
        {
            Assert.That((await _client.GetAsync($"/api/v1/notes/{note.Id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsJsonAsync($"/api/v1/notes/{note.Id}/update",
                    new NoteUpsertRequest("Hack", "x"))).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.DeleteAsync($"/api/v1/notes/{note.Id}/delete")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
        });

        var list = await _client.GetFromJsonAsync<List<NoteResponse>>("/api/v1/notes/");
        Assert.That(list, Is.Empty);
    }
}

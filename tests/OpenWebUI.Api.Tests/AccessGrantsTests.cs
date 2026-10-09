using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do modelo de access grants (read/write por usuário, grupo ou "*")
/// em knowledge, notas, modelos, canais e calendários reais.
/// </summary>
[TestFixture, IsolateEnvironment]
public class AccessGrantsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;
    private AuthResponse _user = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-grants-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("Admin Grants", "admin@grants.local", "senha123");
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        _client.DefaultRequestHeaders.Authorization = null;

        _user = await SignUpAsync("User Grants", "user@grants.local", "senha123");
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

    private async Task<string> CreateKnowledgeAsync(string token, string name)
    {
        UseToken(token);
        var response = await _client.PostAsJsonAsync("/api/v1/knowledge/",
            new CreateKnowledgeRequest(name, null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<KnowledgeResponse>())!.Id;
    }

    private async Task<string> CreateNoteAsync(string token, string title)
    {
        UseToken(token);
        var response = await _client.PostAsJsonAsync("/api/v1/notes/create",
            new NoteUpsertRequest(title, "conteúdo"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var note = await response.Content.ReadFromJsonAsync<JsonElementShim>();
        return note!.Id;
    }

    private sealed record JsonElementShim(string Id);

    [Test, Order(1)]
    public async Task Knowledge_SemGrant_NaoVazaExistencia()
    {
        var id = await CreateKnowledgeAsync(_admin.Token, "Secreta");
        UseToken(_user.Token);

        var get = await _client.GetAsync($"/api/v1/knowledge/{id}");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var list = await _client.GetFromJsonAsync<List<KnowledgeResponse>>("/api/v1/knowledge/");
        Assert.That(list!.Any(k => k.Id == id), Is.False);
    }

    [Test, Order(2)]
    public async Task Knowledge_GrantLeitura_ListaELeMasNaoEscreve()
    {
        var id = await CreateKnowledgeAsync(_admin.Token, "Compartilhada");
        UseToken(_admin.Token);
        var upd = await _client.PostAsJsonAsync($"/api/v1/knowledge/{id}/access/update",
            new AccessUpdateRequest([new AccessGrant("user", _user.User.Id, "read")]));
        Assert.That(upd.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        UseToken(_user.Token);
        var get = await _client.GetAsync($"/api/v1/knowledge/{id}");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = await _client.GetFromJsonAsync<List<KnowledgeResponse>>("/api/v1/knowledge/");
        Assert.That(list!.Any(k => k.Id == id), Is.True);

        // read não escreve
        var write = await _client.PutAsJsonAsync($"/api/v1/knowledge/{id}",
            new UpdateKnowledgeRequest("Renomeada", null));
        Assert.That(write.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Order(3)]
    public async Task Knowledge_GrantEscrita_PermiteUpdate()
    {
        var id = await CreateKnowledgeAsync(_admin.Token, "Editável");
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync($"/api/v1/knowledge/{id}/access/update",
            new AccessUpdateRequest([new AccessGrant("user", _user.User.Id, "write")]));

        UseToken(_user.Token);
        var write = await _client.PutAsJsonAsync($"/api/v1/knowledge/{id}",
            new UpdateKnowledgeRequest("Editada por user", "desc"));
        Assert.That(write.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test, Order(4)]
    public async Task Knowledge_AccessUpdate_SomenteOwnerOuAdmin()
    {
        var id = await CreateKnowledgeAsync(_user.Token, "DoUser");
        UseToken(_admin.Token); // admin pode
        var ok = await _client.PostAsJsonAsync($"/api/v1/knowledge/{id}/access/update",
            new AccessUpdateRequest([]));
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var other = await SignUpAsync("Terceiro", "terc@grants.local", "senha123");
        UseToken(other.Token);
        var forbidden = await _client.PostAsJsonAsync($"/api/v1/knowledge/{id}/access/update",
            new AccessUpdateRequest([new AccessGrant("user", other.User.Id, "read")]));
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        UseToken(_user.Token);
        var invalid = await _client.PostAsJsonAsync($"/api/v1/knowledge/{id}/access/update",
            new AccessUpdateRequest([new AccessGrant("alien", _user.User.Id, "read")]));
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(5)]
    public async Task Nota_GrantEstrela_QualquerAutenticadoLe()
    {
        var noteId = await CreateNoteAsync(_admin.Token, "Nota pública");
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync($"/api/v1/notes/{noteId}/access/update",
            new AccessUpdateRequest([new AccessGrant("user", "*", "read")]));

        var outro = await SignUpAsync("Leitor", "leitor@grants.local", "senha123");
        UseToken(outro.Token);
        var get = await _client.GetAsync($"/api/v1/notes/{noteId}");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // mas não deleta — delete é só do owner
        var del = await _client.DeleteAsync($"/api/v1/notes/{noteId}/delete");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Order(6)]
    public async Task Canal_GrantEstrela_LeMasNaoPosta()
    {
        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/channels/",
            new CreateChannelRequest("canal-publico", null, null));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var channel = await created.Content.ReadFromJsonAsync<ChannelResponse>();
        await _client.PostAsJsonAsync($"/api/v1/channels/{channel!.Id}/access/update",
            new AccessUpdateRequest([new AccessGrant("user", "*", "read")]));

        UseToken(_user.Token);
        var list = await _client.GetFromJsonAsync<List<ChannelResponse>>("/api/v1/channels/");
        var mine = list!.FirstOrDefault(c => c.Id == channel.Id);
        Assert.That(mine, Is.Not.Null);
        Assert.That(mine!.MyRole, Is.EqualTo("viewer"));

        var get = await _client.GetAsync($"/api/v1/channels/{channel.Id}");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var post = await _client.PostAsJsonAsync($"/api/v1/channels/{channel.Id}/messages",
            new CreateChannelMessageRequest("oi", null));
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test, Order(7)]
    public async Task Calendario_CrudEventos_RangeEBusca()
    {
        UseToken(_admin.Token);
        var cal = await _client.PostAsJsonAsync("/api/v1/calendars/",
            new CreateCalendarRequest("Pessoal", "#3b82f6"));
        Assert.That(cal.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var calendar = (await cal.Content.ReadFromJsonAsync<CalendarResponse>())!;

        var start = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        var ev = await _client.PostAsJsonAsync("/api/v1/calendars/events",
            new CreateEventRequest(calendar.Id, "Reunião", start, start + 3600, null, "sala 2"));
        Assert.That(ev.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var range = await _client.GetFromJsonAsync<List<CalendarEventResponse>>(
            $"/api/v1/calendars/events?from={start - 60}&to={start + 60}");
        Assert.That(range!.Any(e => e.Title == "Reunião"), Is.True);

        var search = await _client.GetFromJsonAsync<List<CalendarEventResponse>>(
            "/api/v1/calendars/events/search?query=reunião");
        Assert.That(search!.Any(e => e.CalendarId == calendar.Id), Is.True);

        var del = await _client.DeleteAsync($"/api/v1/calendars/{calendar.Id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var after = await _client.GetFromJsonAsync<List<CalendarEventResponse>>(
            "/api/v1/calendars/events/search?query=Reunião");
        Assert.That(after!.Any(), Is.False);
    }

    [Test, Order(8)]
    public async Task Calendario_GrantGrupo_MembroVeEventos()
    {
        // admin cria grupo com _user como membro
        UseToken(_admin.Token);
        var grp = await _client.PostAsJsonAsync("/api/v1/groups/",
            new CreateGroupRequest("Time", null, null));
        Assert.That(grp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var group = (await grp.Content.ReadFromJsonAsync<GroupResponse>())!;
        await _client.PostAsJsonAsync($"/api/v1/groups/{group.Id}/members",
            new UpdateGroupMembersRequest([_user.User.Id]));

        var cal = await _client.PostAsJsonAsync("/api/v1/calendars/",
            new CreateCalendarRequest("Do time", null));
        var calendar = (await cal.Content.ReadFromJsonAsync<CalendarResponse>())!;
        await _client.PostAsJsonAsync($"/api/v1/calendars/{calendar.Id}/access/update",
            new AccessUpdateRequest([new AccessGrant("group", group.Id, "read")]));

        var start = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await _client.PostAsJsonAsync("/api/v1/calendars/events",
            new CreateEventRequest(calendar.Id, "Daily", start, start + 600, null, null));

        UseToken(_user.Token);
        var cals = await _client.GetFromJsonAsync<List<CalendarResponse>>("/api/v1/calendars/");
        Assert.That(cals!.Any(c => c.Id == calendar.Id), Is.True);

        // read grant não cria evento
        var ev = await _client.PostAsJsonAsync("/api/v1/calendars/events",
            new CreateEventRequest(calendar.Id, "Invasão", start, start + 600, null, null));
        Assert.That(ev.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}

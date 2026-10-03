using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes de integração dos endpoints de calendários e eventos.</summary>
[TestFixture]
public class CalendarEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-cal-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@cal.local", "senha123");
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

    private async Task<string> CriarCalendarioAsync(string nome)
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/calendars/", new CreateCalendarRequest(nome, "#4f9cf9"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<CalendarResponse>())!.Id;
    }

    [Test]
    public async Task Calendarios_SemToken_Retorna401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var response = await _client.GetAsync("/api/v1/calendars/");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task CriarCalendario_SemNome_Retorna400()
    {
        var user = await SignUpAsync("U1", "u1@cal.local", "senha123");
        UseToken(user.Token);
        var response = await _client.PostAsJsonAsync(
            "/api/v1/calendars/", new CreateCalendarRequest("  ", null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task CrudCalendario_FluxoCompleto()
    {
        var user = await SignUpAsync("U2", "u2@cal.local", "senha123");
        UseToken(user.Token);
        var id = await CriarCalendarioAsync("Trabalho");

        var lista = await _client.GetFromJsonAsync<List<CalendarResponse>>("/api/v1/calendars/");
        Assert.That(lista!.Single(c => c.Id == id).Name, Is.EqualTo("Trabalho"));

        var get = await _client.GetFromJsonAsync<CalendarResponse>($"/api/v1/calendars/{id}");
        Assert.That(get!.Name, Is.EqualTo("Trabalho"));

        var updated = await _client.PutAsJsonAsync(
            $"/api/v1/calendars/{id}", new UpdateCalendarRequest("Pessoal", "#00ff00"));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var depois = await _client.GetFromJsonAsync<CalendarResponse>($"/api/v1/calendars/{id}");
        Assert.That(depois!.Name, Is.EqualTo("Pessoal"));

        var del = await _client.DeleteAsync($"/api/v1/calendars/{id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var getFinal = await _client.GetAsync($"/api/v1/calendars/{id}");
        Assert.That(getFinal.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task CalendarioDeOutroUsuario_Retorna404()
    {
        var dono = await SignUpAsync("U3", "u3@cal.local", "senha123");
        UseToken(dono.Token);
        var id = await CriarCalendarioAsync("Privado");

        var outro = await SignUpAsync("U4", "u4@cal.local", "senha123");
        UseToken(outro.Token);
        var get = await _client.GetAsync($"/api/v1/calendars/{id}");
        var put = await _client.PutAsJsonAsync($"/api/v1/calendars/{id}", new UpdateCalendarRequest("x", null));
        var del = await _client.DeleteAsync($"/api/v1/calendars/{id}");
        Assert.Multiple(() =>
        {
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task UpdateAccess_CompartilhaComGrant()
    {
        var dono = await SignUpAsync("U5", "u5@cal.local", "senha123");
        UseToken(dono.Token);
        var id = await CriarCalendarioAsync("Compartilhado");

        var grant = new AccessUpdateRequest([new AccessGrant("user", "*", "read")]);
        var update = await _client.PostAsJsonAsync($"/api/v1/calendars/{id}/access/update", grant);
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var leitor = await SignUpAsync("U6", "u6@cal.local", "senha123");
        UseToken(leitor.Token);
        var get = await _client.GetAsync($"/api/v1/calendars/{id}");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var put = await _client.PutAsJsonAsync(
            $"/api/v1/calendars/{id}", new UpdateCalendarRequest("hack", null));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task UpdateAccess_GrantInvalido_Retorna400()
    {
        var user = await SignUpAsync("U7", "u7@cal.local", "senha123");
        UseToken(user.Token);
        var id = await CriarCalendarioAsync("C1");
        var update = await _client.PostAsJsonAsync(
            $"/api/v1/calendars/{id}/access/update",
            new AccessUpdateRequest([new AccessGrant("et", "*", "flying")]));
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Eventos_CrudEFiltroDePeriodo()
    {
        var user = await SignUpAsync("U8", "u8@cal.local", "senha123");
        UseToken(user.Token);
        var calId = await CriarCalendarioAsync("Agenda");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var created = await _client.PostAsJsonAsync("/api/v1/calendars/events",
            new CreateEventRequest(calId, "Reunião", now + 100, now + 200, "#f00", "nota da reuniao"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var ev = (await created.Content.ReadFromJsonAsync<CalendarEventResponse>())!;

        var todos = await _client.GetFromJsonAsync<List<CalendarEventResponse>>("/api/v1/calendars/events");
        Assert.That(todos!.Any(e => e.Id == ev.Id), Is.True);

        var dentro = await _client.GetFromJsonAsync<List<CalendarEventResponse>>(
            $"/api/v1/calendars/events?from={now}&to={now + 300}");
        Assert.That(dentro!.Single(e => e.Id == ev.Id).Title, Is.EqualTo("Reunião"));

        var fora = await _client.GetFromJsonAsync<List<CalendarEventResponse>>(
            $"/api/v1/calendars/events?from={now + 300}&to={now + 400}");
        Assert.That(fora!.Any(e => e.Id == ev.Id), Is.False);

        var busca = await _client.GetFromJsonAsync<List<CalendarEventResponse>>(
            "/api/v1/calendars/events/search?query=reunião");
        Assert.That(busca!.Any(e => e.Id == ev.Id), Is.True);
        var buscaVazia = await _client.GetFromJsonAsync<List<CalendarEventResponse>>(
            "/api/v1/calendars/events/search?query=naoexiste");
        Assert.That(buscaVazia!.Any(e => e.Id == ev.Id), Is.False);

        var updated = await _client.PutAsJsonAsync($"/api/v1/calendars/events/{ev.Id}",
            new UpdateEventRequest("Reunião adiada", now + 150, now + 250, null, null));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var del = await _client.DeleteAsync($"/api/v1/calendars/events/{ev.Id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var depois = await _client.GetFromJsonAsync<List<CalendarEventResponse>>("/api/v1/calendars/events");
        Assert.That(depois!.Any(e => e.Id == ev.Id), Is.False);
    }

    [Test]
    public async Task Evento_TituloVazioOuIntervaloInvalido_Retorna400()
    {
        var user = await SignUpAsync("U9", "u9@cal.local", "senha123");
        UseToken(user.Token);
        var calId = await CriarCalendarioAsync("C");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var semTitulo = await _client.PostAsJsonAsync("/api/v1/calendars/events",
            new CreateEventRequest(calId, " ", now, now + 10, null, null));
        var invertido = await _client.PostAsJsonAsync("/api/v1/calendars/events",
            new CreateEventRequest(calId, "ok", now + 10, now, null, null));
        var semCalendario = await _client.PostAsJsonAsync("/api/v1/calendars/events",
            new CreateEventRequest("inexistente", "ok", now, now + 10, null, null));

        Assert.Multiple(() =>
        {
            Assert.That(semTitulo.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(invertido.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(semCalendario.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task DeletarCalendario_RemoveEventosEmCascata()
    {
        var user = await SignUpAsync("U10", "u10@cal.local", "senha123");
        UseToken(user.Token);
        var calId = await CriarCalendarioAsync("Temp");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var created = await _client.PostAsJsonAsync("/api/v1/calendars/events",
            new CreateEventRequest(calId, "E", now, now + 10, null, null));
        var ev = (await created.Content.ReadFromJsonAsync<CalendarEventResponse>())!;

        await _client.DeleteAsync($"/api/v1/calendars/{calId}");

        var evDel = await _client.DeleteAsync($"/api/v1/calendars/events/{ev.Id}");
        Assert.That(evDel.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}

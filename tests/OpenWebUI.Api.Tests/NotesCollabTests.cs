using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da edição colaborativa de notas: duas conexões SignalR (dono +
/// usuário com write grant) propagam ops; read-only é rejeitado;
/// versão defasada recebe note:rejected com o estado atual.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class NotesCollabTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _owner = null!;
    private AuthResponse _writer = null!;
    private AuthResponse _reader = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-collab-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _owner = await SignUpAsync("Owner Collab", "owner@collab.local", "senha123");

        // owner é o primeiro usuário → admin; demais signups precisam do papel
        // "user" antes de nascerem, senão ficam pending com token vazio.
        UseToken(_owner.Token);
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        _client.DefaultRequestHeaders.Authorization = null;

        _writer = await SignUpAsync("Writer Collab", "writer@collab.local", "senha123");
        _reader = await SignUpAsync("Reader Collab", "reader@collab.local", "senha123");
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

    private HubConnection BuildHub(string token) =>
        new HubConnectionBuilder()
            .WithUrl("http://localhost/ws", options =>
            {
                // LongPolling funciona sobre o TestServer (sem sockets reais);
                // o AccessTokenProvider injeta o Bearer no negotiate/polling.
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

    private async Task<NoteResponse> CreateSharedNoteAsync()
    {
        UseToken(_owner.Token);
        var create = await _client.PostAsJsonAsync("/api/v1/notes/create",
            new { title = "nota compartilhada", content = "v0" });
        Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await create.Content.ReadAsStringAsync());
        var note = (await create.Content.ReadFromJsonAsync<NoteResponse>())!;

        var grant = await _client.PostAsJsonAsync(
            $"/api/v1/notes/{note.Id}/access/update",
            new AccessUpdateRequest(
            [
                new AccessGrant("user", _writer.User.Id, "write"),
                new AccessGrant("user", _reader.User.Id, "read"),
            ]));
        Assert.That(grant.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await grant.Content.ReadAsStringAsync());

        // access/update bumpa note.UpdatedAt — o objeto "note" do create já
        // está defasado se o segundo virar entre as duas chamadas (flake real
        // sob coverage). Re-lê a nota para a versão atual do servidor.
        var fresh = await _client.GetAsync($"/api/v1/notes/{note.Id}");
        Assert.That(fresh.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await fresh.Content.ReadAsStringAsync());
        return (await fresh.Content.ReadFromJsonAsync<NoteResponse>())!;
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, string what)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.That(completed, Is.SameAs(task), $"Timeout aguardando {what}.");
        return await task;
    }

    [Test]
    public async Task EdicaoRemota_PropagaParaOutraConexao()
    {
        var note = await CreateSharedNoteAsync();
        var ownerHub = BuildHub(_owner.Token);
        var writerHub = BuildHub(_writer.Token);
        var updateTcs = new TaskCompletionSource<(string Text, long Version)>();
        var rosterTcs = new TaskCompletionSource<JsonElement[]>();
        writerHub.On<string, string, string, long>(
            "note:update", (_, _, text, v) => updateTcs.TrySetResult((text, v)));
        writerHub.On<string, JsonElement[]>(
            "note:presence", (_, roster) => rosterTcs.TrySetResult(roster));

        await ownerHub.StartAsync();
        await writerHub.StartAsync();
        try
        {
            await ownerHub.InvokeAsync("JoinNote", note.Id);
            await writerHub.InvokeAsync("JoinNote", note.Id);

            var roster = await WithTimeout(rosterTcs.Task, "roster de presença");
            Assert.That(roster.Length, Is.GreaterThanOrEqualTo(2));

            var newVersion = await ownerHub.InvokeAsync<long>(
                "NoteUpdate", note.Id, "texto do dono", note.UpdatedAt);
            Assert.That(newVersion, Is.GreaterThan(0));

            var (text, version) = await WithTimeout(updateTcs.Task, "note:update");
            Assert.Multiple(() =>
            {
                Assert.That(text, Is.EqualTo("texto do dono"));
                Assert.That(version, Is.EqualTo(newVersion));
            });
        }
        finally
        {
            await ownerHub.DisposeAsync();
            await writerHub.DisposeAsync();
        }
    }

    [Test]
    public async Task ReadOnly_NaoPublicaOps_EVersaoDefasada_Rejeita()
    {
        var note = await CreateSharedNoteAsync();
        var ownerHub = BuildHub(_owner.Token);
        var readerHub = BuildHub(_reader.Token);
        var ownerUpdate = new TaskCompletionSource<string>();
        var rejectedTcs = new TaskCompletionSource<long>();
        ownerHub.On<string, string, string, long>(
            "note:update", (_, _, text, _) => ownerUpdate.TrySetResult(text));
        readerHub.On<string, string, long>(
            "note:rejected", (_, _, v) => rejectedTcs.TrySetResult(v));

        await ownerHub.StartAsync();
        await readerHub.StartAsync();
        try
        {
            await ownerHub.InvokeAsync("JoinNote", note.Id);
            await readerHub.InvokeAsync("JoinNote", note.Id);

            // read-only tenta escrever → invoke retorna -1 e note:rejected chega
            var result = await readerHub.InvokeAsync<long>(
                "NoteUpdate", note.Id, "escrita proibida", note.UpdatedAt);
            Assert.That(result, Is.EqualTo(-1));
            var rejectedVersion = await WithTimeout(rejectedTcs.Task, "note:rejected");
            Assert.That(rejectedVersion, Is.EqualTo(note.UpdatedAt));

            var noBroadcast = await Task.WhenAny(
                ownerUpdate.Task, Task.Delay(TimeSpan.FromMilliseconds(700)));
            Assert.That(noBroadcast, Is.Not.SameAs(ownerUpdate.Task),
                "Edição read-only não pode propagar.");

            // dono escreve com versão correta, depois repete com a MESMA versão → stale
            var v1 = await ownerHub.InvokeAsync<long>(
                "NoteUpdate", note.Id, "v1", note.UpdatedAt);
            Assert.That(v1, Is.GreaterThan(0));
            var stale = await ownerHub.InvokeAsync<long>(
                "NoteUpdate", note.Id, "v2-stale", note.UpdatedAt);
            Assert.That(stale, Is.EqualTo(-1));
        }
        finally
        {
            await ownerHub.DisposeAsync();
            await readerHub.DisposeAsync();
        }
    }
}

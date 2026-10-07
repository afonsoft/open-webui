using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura das rotas de runs desacopladas (SPEC-20261007-chat-detached-runs):
/// enqueue persiste a mensagem do usuário, isolamento por usuário, attach SSE
/// com replay e stop. Sem provider configurado a run vai a <c>failed</c> — o que
/// já prova que o dispatcher executou em background sem a conexão do cliente.
/// </summary>
[TestFixture]
public class ChatRunEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-runs-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        // O primeiro usuário vira admin e habilita "user" como papel padrão —
        // sem isso os signups seguintes ficam pendentes e a API devolve 401.
        var admin = await SignUpAsync("Admin", "admin@runs.local");
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

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, "senha123"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<ChatResponse> CriarChatAsync(List<ChatMessageModel> mensagens)
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Chat runs", ["llama3"], mensagens));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    private async Task<ChatRunResponse> EnfileirarAsync(
        string chatId, string? content, string model = "llama3")
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chatId}/messages",
            new EnqueueChatRunRequest(content, model));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChatRunResponse>())!;
    }

    private async Task<ChatRunResponse> AguardarFinalAsync(string chatId, string runId)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < limite)
        {
            var run = await _client.GetFromJsonAsync<ChatRunResponse>(
                $"/api/v1/chats/{chatId}/runs/{runId}");
            Assert.That(run, Is.Not.Null);
            if (run!.Status is not ("queued" or "running"))
            {
                return run;
            }

            await Task.Delay(150);
        }

        Assert.Fail($"Run {runId} não finalizou em 20s.");
        return null!;
    }

    [Test]
    public async Task Enqueue_PersisteMensagemDoUsuarioEDespachaRun()
    {
        var auth = await SignUpAsync("RunA", "runa@runs.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync([]);

        var run = await EnfileirarAsync(chat.Id, "olá");

        Assert.Multiple(() =>
        {
            Assert.That(run.ChatId, Is.EqualTo(chat.Id));
            Assert.That(run.Status, Is.AnyOf("queued", "running", "failed"));
        });

        // A mensagem do usuário foi persistida no servidor no enqueue.
        var detalhe = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{chat.Id}");
        Assert.That(detalhe!.Messages.Select(m => (m.Role, m.Content)),
            Contains.Item(("user", "olá")));

        // Sem provider configurado o dispatcher leva a run a failed — prova que
        // a execução aconteceu em background, independente da conexão do cliente.
        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("failed"));
        Assert.That(final.Error, Is.Not.Null.And.Not.Empty);
        Assert.That(final.CompletedAt, Is.Not.Null);
    }

    [Test]
    public async Task Enqueue_SemContent_ExigeHistoricoTerminandoEmUsuario()
    {
        var auth = await SignUpAsync("RunB", "runb@runs.local");
        UseToken(auth.Token);

        // Histórico termina em assistant → regeneração rejeitada.
        var chat = await CriarChatAsync([
            new ChatMessageModel("m1", "user", "pergunta", null, 100),
            new ChatMessageModel("m2", "assistant", "resposta", "llama3", 101),
        ]);
        var bad = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/messages", new EnqueueChatRunRequest(null, "llama3"));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Histórico terminando em user → aceita e enfileira.
        var chatOk = await CriarChatAsync([
            new ChatMessageModel("m1", "user", "pergunta", null, 100),
        ]);
        var ok = await EnfileirarAsync(chatOk.Id, null);
        Assert.That(ok.Status, Is.AnyOf("queued", "running", "failed"));
        await AguardarFinalAsync(chatOk.Id, ok.Id);
    }

    [Test]
    public async Task Runs_IsolamentoPorUsuario()
    {
        var dono = await SignUpAsync("RunC", "runc@runs.local");
        UseToken(dono.Token);
        var chat = await CriarChatAsync([]);
        var run = await EnfileirarAsync(chat.Id, "segredo");

        var outro = await SignUpAsync("RunD", "rund@runs.local");
        UseToken(outro.Token);

        var get = await _client.GetAsync($"/api/v1/chats/{chat.Id}/runs/{run.Id}");
        var list = await _client.GetFromJsonAsync<List<ChatRunResponse>>(
            $"/api/v1/chats/{chat.Id}/runs");
        var stream = await _client.GetAsync($"/api/v1/chats/{chat.Id}/runs/{run.Id}/stream");
        var stop = await _client.PostAsync($"/api/v1/chats/{chat.Id}/runs/{run.Id}/stop", null);

        Assert.Multiple(() =>
        {
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(list, Is.Empty);
            Assert.That(stream.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(stop.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task Stop_RunEnfileiradaOuRodando_Finaliza()
    {
        var auth = await SignUpAsync("RunE", "rune@runs.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync([]);
        var run = await EnfileirarAsync(chat.Id, "stop me");

        var stop = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/stop", null);

        // Se a run ainda estava queued/running → 200; se já falhou (sem provider) → 409.
        Assert.That(stop.StatusCode,
            Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.Conflict));

        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.AnyOf("stopped", "failed"));

        // Run já finalizada → stop devolve 409.
        var again = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/stop", null);
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task Stream_RunFinalizada_RetornaEFecha()
    {
        var auth = await SignUpAsync("RunF", "runf@runs.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync([]);
        var run = await EnfileirarAsync(chat.Id, "conteúdo");
        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("failed"));

        // Attach a run finalizada: o backlog (erro + DONE) sai e o stream fecha.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/chats/{chat.Id}/runs/{run.Id}/stream");
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType,
            Is.EqualTo("text/event-stream"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("id: "));
            Assert.That(body, Does.Contain("data: [DONE]"));
        });
    }

    [Test]
    public async Task RunsActive_SemRunEmAndamento_RetornaVazio()
    {
        var auth = await SignUpAsync("RunG", "rung@runs.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync([]);

        var response = await _client.GetAsync($"/api/v1/chats/{chat.Id}/runs/active");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty.Or.EqualTo("null"));
    }
}

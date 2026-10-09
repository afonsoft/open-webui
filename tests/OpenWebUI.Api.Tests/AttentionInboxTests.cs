using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Api.Runs;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261009-attention-inbox (D2): chats com run bloqueada esperando o
/// dono (aprovação/pergunta) aparecem no filtro <c>?attention=1</c>, com a
/// flag <c>awaiting</c> na listagem e no endpoint de contagem. A pendência é
/// registrada direto no gate <see cref="ChatRunApprovals"/> — mesmo
/// mecanismo que a run usa ao pausar numa tool.
/// </summary>
[TestFixture, IsolateEnvironment]
public class AttentionInboxTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private ChatRunApprovals _approvals = null!;
    private string _dbPath = null!;
    private readonly List<CancellationTokenSource> _waits = [];

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-attention-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _approvals = _factory.Services.GetRequiredService<ChatRunApprovals>();

        var admin = await SignUpAsync("Admin", "admin@attention.local");
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
        foreach (var wait in _waits)
        {
            wait.Cancel();
            wait.Dispose();
        }

        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
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

    private async Task<ChatResponse> CriarChatAsync(string titulo)
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest(titulo, ["llama3"], []));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    /// <summary>
    /// Registra uma espera pendente no gate — a call só sai da lista quando
    /// o dono resolve (<see cref="ChatRunApprovals.Resolve"/>), expira ou a
    /// run é cancelada. O token do CTS devolvido cancela no teardown.
    /// </summary>
    private CancellationTokenSource RegistrarPendente(string runId, string chatId, string toolName)
    {
        var cts = new CancellationTokenSource();
        _waits.Add(cts);
        _ = _approvals.WaitAsync(runId, chatId, $"call-{runId}", toolName, cts.Token);
        return cts;
    }

    private async Task<List<ChatSummaryResponse>> ListarAsync(string query = "")
    {
        var list = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>(
            $"/api/v1/chats/{query}");
        Assert.That(list, Is.Not.Null);
        return list!;
    }

    private async Task<int> ContagemAsync()
    {
        var result = await _client.GetFromJsonAsync<AttentionCountResponse>(
            "/api/v1/chats/attention/count");
        Assert.That(result, Is.Not.Null);
        return result!.Count;
    }

    [Test]
    public async Task Attention_AprovacaoPendente_ListaContaESome()
    {
        var auth = await SignUpAsync("AttA", "atta@attention.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Com aprovação");
        var runId = Guid.NewGuid().ToString("N");
        RegistrarPendente(runId, chat.Id, "builtin:file_write");

        // Filtro server-side: o chat aguardando aparece.
        var filtrado = await ListarAsync("?attention=1");
        Assert.That(filtrado.Select(c => c.Id), Does.Contain(chat.Id),
            "?attention=1 deve listar o chat com aprovação pendente");

        // A listagem normal traz a flag awaiting só nesse chat.
        var lista = await ListarAsync();
        Assert.That(lista.First(c => c.Id == chat.Id).Awaiting, Is.True,
            "chat com pendência vem marcado");
        Assert.That(lista.Count(c => c.Awaiting), Is.EqualTo(1));

        var count = await ContagemAsync();
        Assert.That(count, Is.EqualTo(1), "badge conta o chat aguardando");

        // Resolve (aprovado) → sai do filtro, perde a flag e zera a contagem.
        Assert.That(
            _approvals.Resolve(runId, chat.Id, $"call-{runId}", approved: true, remember: false),
            Is.True, "a pendência devia estar registrada");

        // O Resolve completa o TCS; a remoção do _pending acontece no
        // finally do WaitAsync (continuação assíncrona) — poll curto.
        List<ChatSummaryResponse> depois;
        var tentativas = 0;
        do
        {
            depois = await ListarAsync("?attention=1");
            if (!depois.Any(c => c.Id == chat.Id))
            {
                break;
            }

            await Task.Delay(50);
        }
        while (++tentativas < 40);
        Assert.That(depois.Select(c => c.Id), Does.Not.Contain(chat.Id),
            "resolvido some do filtro");
        var listaDepois = await ListarAsync();
        Assert.That(listaDepois.First(c => c.Id == chat.Id).Awaiting, Is.False);
        Assert.That(await ContagemAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task Attention_PerguntaAskUser_Lista()
    {
        var auth = await SignUpAsync("AttB", "attb@attention.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Com pergunta");
        var runId = Guid.NewGuid().ToString("N");
        // ask_user e plan_ent passam pelo mesmo WaitAsync do gate.
        RegistrarPendente(runId, chat.Id, "builtin:ask_user");

        var filtrado = await ListarAsync("?attention=1");
        Assert.That(filtrado.Select(c => c.Id), Does.Contain(chat.Id),
            "pergunta pendente também entra na inbox");

        _approvals.Cancel(runId);
    }

    [Test]
    public async Task Attention_ChatNormal_ExcluidoDaInbox()
    {
        var auth = await SignUpAsync("AttC", "attc@attention.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Sem pendência");

        var filtrado = await ListarAsync("?attention=1");
        Assert.That(filtrado.Select(c => c.Id), Does.Not.Contain(chat.Id));
        var lista = await ListarAsync();
        Assert.That(lista.First(c => c.Id == chat.Id).Awaiting, Is.False);
    }

    [Test]
    public async Task Attention_PendenciaDeOutroUsuario_NaoAparece()
    {
        var authA = await SignUpAsync("AttD", "attd@attention.local");
        var authB = await SignUpAsync("AttE", "atte@attention.local");

        // Chat e pendência do usuário B.
        UseToken(authB.Token);
        var chatB = await CriarChatAsync("Pendente do B");
        var runB = Guid.NewGuid().ToString("N");
        RegistrarPendente(runB, chatB.Id, "builtin:shell_exec");
        await CriarChatAsync("Normal do B");

        // A não vê nada do B — nem no filtro, nem na contagem, nem na flag.
        UseToken(authA.Token);
        await CriarChatAsync("Normal do A");
        var filtrado = await ListarAsync("?attention=1");
        Assert.That(filtrado.Select(c => c.Id), Is.Not.Contains(chatB.Id),
            "pendência de outro usuário não vaza");
        Assert.That(await ContagemAsync(), Is.EqualTo(0));
        var listaA = await ListarAsync();
        Assert.That(listaA.All(c => !c.Awaiting), Is.True);

        // B vê só o dele aguardando.
        UseToken(authB.Token);
        var filtradoB = await ListarAsync("?attention=1");
        Assert.That(filtradoB.Select(c => c.Id), Is.EqualTo(new[] { chatB.Id }));
        Assert.That(await ContagemAsync(), Is.EqualTo(1));

        _approvals.Cancel(runB);
    }
}

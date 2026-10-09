using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Cobertura de branches de borda/erro dos endpoints de automações.</summary>
[TestFixture, IsolateEnvironment]
public class AutomationEdgeTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-autoedge-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@edge.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);

        var baseUrl = StartMock();
        var connections = new ConnectionsConfig([baseUrl], [], []);
        var connResponse = await _client.PostAsJsonAsync("/api/v1/configs/connections", connections);
        Assert.That(connResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mockCts.Cancel();
        _mock.Stop();
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private string StartMock()
    {
        var random = new Random();
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port = random.Next(40000, 60000);
            _mock = new HttpListener();
            _mock.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _mock.Start();
                break;
            }
            catch (HttpListenerException)
            {
                _mock.Close();
            }
        }
        if (!_mock.IsListening)
        {
            throw new InvalidOperationException("Nenhuma porta livre para o mock.");
        }
        _mockCts = new CancellationTokenSource();
        _ = Task.Run(() => MockLoopAsync(_mockCts.Token));
        return $"http://localhost:{port}";
    }

    private async Task MockLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _mock.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }

            var path = ctx.Request.Url!.AbsolutePath;
            var (status, json) = path switch
            {
                "/api/tags" => (200, "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}"),
                "/api/chat" => (200, "{\"message\":{\"content\":\"resposta do mock\"}}"),
                _ => (404, "{}"),
            };
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email, string password)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, password));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static AutomationUpsertRequest NovaAutomacao(
        string kind = "interval", int? minutes = 1,
        string? time = null, int? weekday = null,
        string model = "fake:1", bool? enabled = true,
        string name = "Lembrete", string prompt = "Diga olá") =>
        new(name, prompt, model, kind, minutes, time, weekday, enabled);

    private async Task<AutomationResponse> CriarAutomacaoAsync(
        AutomationUpsertRequest? request = null)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/automations", request ?? NovaAutomacao());
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AutomationResponse>())!;
    }

    [Test, Order(1)]
    public async Task Validacao_CamposObrigatorios_Retorna400()
    {
        var user = await SignUpAsync("Val1", "val1@edge.local", "senha123");
        UseToken(user.Token);

        var semNome = await _client.PostAsJsonAsync("/api/v1/automations",
            NovaAutomacao(name: " "));
        Assert.That(semNome.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var semPrompt = await _client.PostAsJsonAsync("/api/v1/automations",
            NovaAutomacao(prompt: ""));
        Assert.That(semPrompt.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var semModelo = await _client.PostAsJsonAsync("/api/v1/automations",
            NovaAutomacao(model: " "));
        Assert.That(semModelo.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(2)]
    public async Task Validacao_ScheduleKindInvalido_Retorna400NoCreateENoUpdate()
    {
        var user = await SignUpAsync("Val2", "val2@edge.local", "senha123");
        UseToken(user.Token);

        var create = await _client.PostAsJsonAsync("/api/v1/automations",
            NovaAutomacao(kind: "hourly"));
        Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // A mesma validação roda no update (branch error != null de UpdateAsync).
        var automation = await CriarAutomacaoAsync();
        var update = await _client.PutAsJsonAsync($"/api/v1/automations/{automation.Id}",
            NovaAutomacao(kind: "hourly"));
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // O registro permanece intacto após o update rejeitado.
        var atual = await _client.GetFromJsonAsync<AutomationResponse>(
            $"/api/v1/automations/{automation.Id}");
        Assert.That(atual!.ScheduleKind, Is.EqualTo("interval"));
    }

    [Test, Order(3)]
    public async Task Validacao_IntervalAbaixoDoMinimo_Retorna400()
    {
        var user = await SignUpAsync("Val3", "val3@edge.local", "senha123");
        UseToken(user.Token);

        foreach (var minutes in new int?[] { null, 0, -5 })
        {
            var response = await _client.PostAsJsonAsync("/api/v1/automations",
                NovaAutomacao(minutes: minutes));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
                $"IntervalMinutes={minutes}");
        }
    }

    [Test, Order(4)]
    public async Task Validacao_DailySemHoraOuHoraInvalida_Retorna400()
    {
        var user = await SignUpAsync("Val4", "val4@edge.local", "senha123");
        UseToken(user.Token);

        var semHora = await _client.PostAsJsonAsync("/api/v1/automations",
            NovaAutomacao(kind: "daily", minutes: null));
        Assert.That(semHora.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var horaInvalida = await _client.PostAsJsonAsync("/api/v1/automations",
            NovaAutomacao(kind: "daily", minutes: null, time: "25h99"));
        Assert.That(horaInvalida.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(5)]
    public async Task Validacao_WeeklySemDiaOuDiaForaDe0a6_Retorna400()
    {
        var user = await SignUpAsync("Val5", "val5@edge.local", "senha123");
        UseToken(user.Token);

        foreach (var weekday in new int?[] { null, -1, 7 })
        {
            var response = await _client.PostAsJsonAsync("/api/v1/automations",
                NovaAutomacao(kind: "weekly", minutes: null, time: "08:30", weekday: weekday));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
                $"Weekday={weekday}");
        }
    }

    [Test, Order(6)]
    public async Task SemToken_TodasAsRotas_Retornam401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var id = Guid.NewGuid().ToString("N");

        Assert.Multiple(async () =>
        {
            Assert.That((await _client.GetAsync("/api/v1/automations")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await _client.PostAsJsonAsync("/api/v1/automations", NovaAutomacao())).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await _client.GetAsync("/api/v1/automations/runs?from=0&to=1")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await _client.GetAsync($"/api/v1/automations/{id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await _client.PutAsJsonAsync($"/api/v1/automations/{id}", NovaAutomacao())).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await _client.DeleteAsync($"/api/v1/automations/{id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await _client.GetAsync($"/api/v1/automations/{id}/runs")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await _client.PostAsync($"/api/v1/automations/{id}/run-now", null)).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
        });
    }

    [Test, Order(7)]
    public async Task TokenDeUsuarioRemovido_Handlers_Retornam401()
    {
        // JWT continua válido, mas o usuário foi removido do banco:
        // FindUserAsync retorna null e cada handler responde 401.
        var ghost = await SignUpAsync("Ghost", "ghost@edge.local", "senha123");

        UseToken(_adminToken);
        var deleted = await _client.DeleteAsync($"/api/v1/users/{ghost.User.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        UseToken(ghost.Token);
        Assert.Multiple(async () =>
        {
            Assert.That((await _client.GetAsync("/api/v1/automations")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await _client.PostAsJsonAsync("/api/v1/automations", NovaAutomacao())).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await _client.GetAsync("/api/v1/automations/runs?from=0&to=1")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await _client.GetAsync(
                $"/api/v1/automations/{Guid.NewGuid():N}")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
        });
    }

    [Test, Order(8)]
    public async Task AutomacaoInexistente_Operacoes_Retornam404()
    {
        var user = await SignUpAsync("NotFound", "nf@edge.local", "senha123");
        UseToken(user.Token);
        var id = Guid.NewGuid().ToString("N");

        Assert.Multiple(async () =>
        {
            Assert.That((await _client.GetAsync($"/api/v1/automations/{id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PutAsJsonAsync($"/api/v1/automations/{id}", NovaAutomacao())).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.DeleteAsync($"/api/v1/automations/{id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.GetAsync($"/api/v1/automations/{id}/runs")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsync($"/api/v1/automations/{id}/run-now", null)).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test, Order(9)]
    public async Task AutomacaoDeOutroUsuario_TodasOperacoes_Retornam404()
    {
        var owner = await SignUpAsync("Owner", "owner@edge.local", "senha123");
        UseToken(owner.Token);
        var automation = await CriarAutomacaoAsync();

        var other = await SignUpAsync("Other", "other@edge.local", "senha123");
        UseToken(other.Token);

        // FindOwnedAsync filtra por UserId: recurso alheio é 404, nunca 403.
        Assert.Multiple(async () =>
        {
            Assert.That((await _client.GetAsync($"/api/v1/automations/{automation.Id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PutAsJsonAsync(
                $"/api/v1/automations/{automation.Id}", NovaAutomacao())).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.DeleteAsync($"/api/v1/automations/{automation.Id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.GetAsync($"/api/v1/automations/{automation.Id}/runs")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsync(
                $"/api/v1/automations/{automation.Id}/run-now", null)).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
        });

        // A listagem do outro usuário também não inclui a automação.
        var list = await _client.GetFromJsonAsync<List<AutomationResponse>>("/api/v1/automations");
        Assert.That(list!.Any(a => a.Id == automation.Id), Is.False);
    }

    [Test, Order(10)]
    public async Task RunNow_AutomacaoDesabilitada_ExecutaSemReagendar()
    {
        var user = await SignUpAsync("Disabled", "disabled@edge.local", "senha123");
        UseToken(user.Token);
        var automation = await CriarAutomacaoAsync();

        var disabled = await _client.PutAsJsonAsync($"/api/v1/automations/{automation.Id}",
            NovaAutomacao(enabled: false));
        Assert.That(disabled.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // run-now não checa Enabled: executa mesmo com a automação desligada.
        var runResponse = await _client.PostAsync(
            $"/api/v1/automations/{automation.Id}/run-now", null);
        Assert.That(runResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var run = await runResponse.Content.ReadFromJsonAsync<AutomationRunResponse>();
        Assert.That(run!.Status, Is.EqualTo("ok"));

        // Reschedule com Enabled=false mantém NextRunAt nulo.
        var refreshed = await _client.GetFromJsonAsync<AutomationResponse>(
            $"/api/v1/automations/{automation.Id}");
        Assert.Multiple(() =>
        {
            Assert.That(refreshed!.Enabled, Is.False);
            Assert.That(refreshed.NextRunAt, Is.Null);
        });
    }

    [Test, Order(11)]
    public async Task Update_Parcial_PreservaEnabledEAplicaDefaults()
    {
        var user = await SignUpAsync("Partial", "partial@edge.local", "senha123");
        UseToken(user.Token);

        // daily sem IntervalMinutes nem Enabled: Apply assume 60 e mantém Enabled=true.
        var automation = await CriarAutomacaoAsync(
            NovaAutomacao(kind: "daily", minutes: null, time: "08:30", enabled: null));
        Assert.Multiple(() =>
        {
            Assert.That(automation.Enabled, Is.True);
            Assert.That(automation.IntervalMinutes, Is.EqualTo(60));
            Assert.That(automation.NextRunAt, Is.Not.Null);
        });

        // Desabilita.
        var off = await _client.PutAsJsonAsync($"/api/v1/automations/{automation.Id}",
            NovaAutomacao(kind: "daily", minutes: null, time: "08:30", enabled: false));
        Assert.That(off.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Update com Enabled=null preserva o estado anterior (desabilitada).
        var partial = await _client.PutAsJsonAsync($"/api/v1/automations/{automation.Id}",
            NovaAutomacao(kind: "daily", minutes: null, time: "09:45",
                enabled: null, name: "Renomeada"));
        Assert.That(partial.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var updated = await partial.Content.ReadFromJsonAsync<AutomationResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(updated!.Enabled, Is.False);
            Assert.That(updated.NextRunAt, Is.Null);
            Assert.That(updated.Name, Is.EqualTo("Renomeada"));
            Assert.That(updated.TimeOfDay, Is.EqualTo("09:45"));
        });
    }

    [Test, Order(12)]
    public async Task Calendario_RangeInvertidoOuInvalido()
    {
        var user = await SignUpAsync("Cal", "cal@edge.local", "senha123");
        UseToken(user.Token);
        var automation = await CriarAutomacaoAsync();

        var runResponse = await _client.PostAsync(
            $"/api/v1/automations/{automation.Id}/run-now", null);
        Assert.That(runResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // from > to: nenhum run satisfaz StartedAt >= from && <= to.
        var invertido = await _client.GetFromJsonAsync<List<AutomationRunResponse>>(
            $"/api/v1/automations/runs?from={now + 3600}&to={now - 3600}");
        Assert.That(invertido, Is.Empty);

        // Parâmetros ausentes ou não numéricos falham no binding (400).
        var semParams = await _client.GetAsync("/api/v1/automations/runs");
        Assert.That(semParams.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var naoNumerico = await _client.GetAsync("/api/v1/automations/runs?from=abc&to=1");
        Assert.That(naoNumerico.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(13)]
    public async Task Calendario_RunsDeOutroUsuario_NaoAparecem()
    {
        var owner = await SignUpAsync("CalOwner", "calowner@edge.local", "senha123");
        UseToken(owner.Token);
        var automation = await CriarAutomacaoAsync();
        var runResponse = await _client.PostAsync(
            $"/api/v1/automations/{automation.Id}/run-now", null);
        Assert.That(runResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var other = await SignUpAsync("CalOther", "calother@edge.local", "senha123");
        UseToken(other.Token);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var runs = await _client.GetFromJsonAsync<List<AutomationRunResponse>>(
            $"/api/v1/automations/runs?from={now - 3600}&to={now + 3600}");
        Assert.That(runs, Is.Empty);
    }

    [Test, Order(14)]
    public async Task ListRuns_AutomacaoSemExecucoes_RetornaListaVazia()
    {
        var user = await SignUpAsync("Empty", "empty@edge.local", "senha123");
        UseToken(user.Token);
        var automation = await CriarAutomacaoAsync();

        var runs = await _client.GetFromJsonAsync<List<AutomationRunResponse>>(
            $"/api/v1/automations/{automation.Id}/runs");
        Assert.That(runs, Is.Empty);
    }
}

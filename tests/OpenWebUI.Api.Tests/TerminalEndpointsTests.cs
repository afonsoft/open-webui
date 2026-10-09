using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da slice terminals-jupyter: CRUD admin de terminal servers,
/// proxy HTTP com sanitização de path e túnel WebSocket bidirecional.
/// </summary>
[TestFixture]
[NonParallelizable]
public class TerminalEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _mockBase = null!;
    private WebApplication _wsEcho = null!;
    private int _wsPort;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-term-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@term.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });

        _mockBase = StartHttpMock();
        (_wsEcho, _wsPort) = await StartWsEchoAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        _mockCts.Cancel();
        _mock.Stop();
        await _wsEcho.StopAsync();
        await _wsEcho.DisposeAsync();
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private string StartHttpMock()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var port = new Random().Next(40000, 60000);
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
            throw new InvalidOperationException("Nenhuma porta livre para o mock HTTP.");
        }
        _mockCts = new CancellationTokenSource();
        _ = Task.Run(() => HttpMockLoopAsync(_mockCts.Token));
        var prefix = _mock.Prefixes.First();
        return prefix.TrimEnd('/');
    }

    private static async Task HttpMockLoopBody(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        var (status, body) = path switch
        {
            "/api/kernels" => (200, "{\"id\":\"kernel-1\",\"name\":\"python3\"}"),
            "/api/auth-check" => ctx.Request.Headers["Authorization"] == "Bearer term-key"
                ? (200, "{\"ok\":true}")
                : (401, "{\"ok\":false}"),
            _ => (404, "{}"),
        };
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private async Task HttpMockLoopAsync(CancellationToken ct)
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

            await HttpMockLoopBody(ctx);
        }
    }

    /// <summary>Servidor WebSocket real (Kestrel) que ecoa frames de volta.
    /// Sem using: o app é devolvido vivo e o fixture o dispõe no teardown —
    /// dispose aqui mataria o echo antes do teste conectar.</summary>
    private static async Task<(WebApplication App, int Port)> StartWsEchoAsync()
    {
        var port = new Random().Next(40000, 60000);
        var app = WebApplication.Create();
        app.Urls.Add($"http://localhost:{port}");
        app.UseWebSockets();
        app.Map("/{**p}", async http =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = 400;
                return;
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open)
            {
                var message = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (message.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(message.CloseStatus!.Value,
                        message.CloseStatusDescription, CancellationToken.None);
                    break;
                }

                await socket.SendAsync(
                    buffer.AsMemory(0, message.Count), message.MessageType,
                    message.EndOfMessage, CancellationToken.None);
            }
        });
        await app.StartAsync();
        return (app, port);
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

    [Test, Order(1)]
    public async Task Config_CrudAdmin_EListagemSemKey()
    {
        UseToken(_adminToken);

        // Cria servidor com key
        var created = await _client.PostAsJsonAsync("/api/v1/terminals/config",
            new TerminalServerRequest("Mock Jupyter", _mockBase, "token", "term-key"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var server = await created.Content.ReadFromJsonAsync<TerminalServerResponse>();
        Assert.That(server!.Id, Is.EqualTo("mock-jupyter"));
        Assert.That(server.HasKey, Is.True);

        // Lista não expõe a key
        var list = await _client.GetFromJsonAsync<List<TerminalServerResponse>>("/api/v1/terminals/");
        Assert.That(list, Has.Count.EqualTo(1));
        var raw = await _client.GetAsync("/api/v1/terminals/");
        Assert.That(await raw.Content.ReadAsStringAsync(), Does.Not.Contain("term-key"));

        // Validações
        var badUrl = await _client.PostAsJsonAsync("/api/v1/terminals/config",
            new TerminalServerRequest("x", "ftp://nao", "token", null));
        Assert.That(badUrl.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var badAuth = await _client.PostAsJsonAsync("/api/v1/terminals/config",
            new TerminalServerRequest("x", _mockBase, "oauth", null));
        Assert.That(badAuth.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Usuário comum não configura
        var user = await SignUpAsync("U", "u@term.local", "senha123");
        UseToken(user.Token);
        var forbidden = await _client.PostAsJsonAsync("/api/v1/terminals/config",
            new TerminalServerRequest("y", _mockBase, "none", null));
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        UseToken(_adminToken);
    }

    [Test, Order(2)]
    public async Task Proxy_RepassaGetEPost_ComAuthDoServidor()
    {
        UseToken(_adminToken);
        // POST proxied: servidor retorna o kernel criado
        var response = await _client.PostAsJsonAsync("/api/v1/terminals/mock-jupyter/api/kernels", new { });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("kernel-1"));

        // GET proxied com auth do servidor injetada (key mascarada nunca sai)
        var auth = await _client.GetAsync("/api/v1/terminals/mock-jupyter/api/auth-check");
        Assert.That(auth.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test, Order(3)]
    public async Task Proxy_PathTraversalBloqueado_EServidorInexistente404()
    {
        UseToken(_adminToken);
        var traversal = await _client.GetAsync("/api/v1/terminals/mock-jupyter/api/../config");
        Assert.That(traversal.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var outside = await _client.GetAsync("/api/v1/terminals/mock-jupyter/contents/x.ipynb");
        Assert.That(outside.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var missing = await _client.GetAsync("/api/v1/terminals/nao-existe/api/kernels");
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Order(4)]
    public async Task Proxy_ServidorFora_502()
    {
        UseToken(_adminToken);
        await _client.PostAsJsonAsync("/api/v1/terminals/config",
            new TerminalServerRequest("Dead", "http://localhost:1", "none", null));
        var response = await _client.GetAsync("/api/v1/terminals/dead/api/kernels");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
    }

    [Test, Order(5)]
    public async Task WebSocket_Tunnel_EcoaFramesBidirecional()
    {
        await _client.PostAsJsonAsync("/api/v1/terminals/config",
            new TerminalServerRequest("Echo WS", $"http://localhost:{_wsPort}", "none", null));

        var wsClient = _factory.Server.CreateWebSocketClient();
        var uri = new Uri(
            $"ws://localhost/api/v1/terminals/echo-ws/api/terminals/sess-1?access_token={_adminToken}");
        using var socket = await wsClient.ConnectAsync(uri, CancellationToken.None);
        Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));

        // Cliente → servidor → cliente: o echo devolve o mesmo payload.
        var payload = Encoding.UTF8.GetBytes("ping-jupyter");
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[8192];
        var reply = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.That(Encoding.UTF8.GetString(buffer, 0, reply.Count), Is.EqualTo("ping-jupyter"));

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fim", CancellationToken.None);
    }
}

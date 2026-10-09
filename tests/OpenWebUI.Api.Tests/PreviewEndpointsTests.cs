using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do port preview (SPEC-20261009-port-preview, E16 D4):
/// proxy <c>ANY /preview/{port}/**</c> → 127.0.0.1 — echo via
/// <see cref="HttpListener"/> em porta alta (método/path/query/body
/// preservados, Authorization nunca encaminhado, Set-Cookie reescrito),
/// 502 em porta fechada, 400 fora da faixa 1024–65535, 401 anônimo e a
/// feature flag <c>preview.enabled</c> (<c>/api/v1/preview/config</c>).
/// </summary>
public class PreviewEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;
    private AuthResponse _user = null!;
    private readonly List<HttpListener> _listeners = [];

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-pv-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("PvAdmin", "admin@pv.local", "senha123");
        UseToken(_admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());

        _user = await SignUpAsync("PvUser", "user@pv.local", "senha123");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        foreach (var listener in _listeners)
        {
            try { listener.Stop(); } catch (ObjectDisposedException) { }
        }
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
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>Sobe um echo server em porta alta livre (retry em colisão).</summary>
    private HttpListener StartEcho(out int port, bool setCookie = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException) when (attempt < 4)
            {
                continue;
            }

            _listeners.Add(listener);
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await listener.GetContextAsync();
                    }
                    catch (HttpListenerException) { break; }
                    catch (ObjectDisposedException) { break; }
                    _ = Task.Run(() => HandleEchoAsync(ctx, setCookie));
                }
            });
            return listener;
        }
    }

    /// <summary>Echo: devolve método/path+query em headers e o body no corpo.</summary>
    private static async Task HandleEchoAsync(HttpListenerContext ctx, bool setCookie)
    {
        var req = ctx.Request;
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
        {
            body = await reader.ReadToEndAsync();
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.Headers["X-Echo-Method"] = req.HttpMethod;
        ctx.Response.Headers["X-Echo-Path"] = req.Url!.PathAndQuery;
        ctx.Response.Headers["X-Echo-Auth"] = req.Headers["Authorization"] ?? "none";
        ctx.Response.Headers["X-Echo-Host"] = req.Headers["Host"] ?? "none";
        if (setCookie)
        {
            ctx.Response.Headers["Set-Cookie"] = "sid=abc; Path=/; SameSite=Lax; HttpOnly";
        }

        var bytes = Encoding.UTF8.GetBytes($"echo:{body}");
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    // ---------------- flag / auth gates ----------------

    [Test]
    public async Task Preview_SemAuth_401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        Assert.That((await _client.GetAsync("/api/v1/preview/config")).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await _client.GetAsync("/preview/3000/")).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Preview_Config_GetEPut_RequerAdmin()
    {
        UseToken(_user.Token);
        var get = await _client.GetAsync("/api/v1/preview/config");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var node = await get.Content.ReadFromJsonAsync<JsonElementShim>();
        Assert.That(node!.Enabled, Is.True); // default ON

        var put = await _client.PutAsJsonAsync(
            "/api/v1/preview/config", new { enabled = false });
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_admin.Token);
        var putAdmin = await _client.PutAsJsonAsync(
            "/api/v1/preview/config", new { enabled = true });
        Assert.That(putAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Preview_FlagOff_404()
    {
        UseToken(_admin.Token);
        var off = await _client.PutAsJsonAsync(
            "/api/v1/preview/config", new { enabled = false });
        Assert.That(off.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        try
        {
            UseToken(_user.Token);
            var get = await _client.GetAsync("/preview/3000/");
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        finally
        {
            UseToken(_admin.Token);
            var on = await _client.PutAsJsonAsync(
                "/api/v1/preview/config", new { enabled = true });
            Assert.That(on.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
    }

    // ---------------- proxy ----------------

    [Test]
    public async Task Preview_PortaForaDaFaixa_400()
    {
        UseToken(_user.Token);
        foreach (var port in new[] { 1, 80, 1023, 65536, 70000 })
        {
            var response = await _client.GetAsync($"/preview/{port}/");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
                $"porta {port} devia dar 400");
        }
    }

    [Test]
    public async Task Preview_PortaFechada_502()
    {
        UseToken(_user.Token);
        var port = FreePort(); // ninguém escutando
        var response = await _client.GetAsync($"/preview/{port}/");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
    }

    [Test]
    public async Task Preview_Echo_PreservaMetodoPathQueryEBody()
    {
        UseToken(_user.Token);
        StartEcho(out var port);

        var post = new HttpRequestMessage(HttpMethod.Post,
            $"/preview/{port}/deep/path/leaf?x=1&y=dois")
        {
            Content = new StringContent("corpo-teste", Encoding.UTF8, "text/plain"),
        };
        var response = await _client.SendAsync(post);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.GetValues("X-Echo-Method").Single(),
            Is.EqualTo("POST"));
        Assert.That(response.Headers.GetValues("X-Echo-Path").Single(),
            Is.EqualTo("/deep/path/leaf?x=1&y=dois"));
        Assert.That(response.Headers.GetValues("X-Echo-Host").Single(),
            Is.EqualTo($"127.0.0.1:{port}"));
        Assert.That(await response.Content.ReadAsStringAsync(),
            Is.EqualTo("echo:corpo-teste"));
    }

    [Test]
    public async Task Preview_AuthorizationNaoEncaminhado()
    {
        UseToken(_user.Token);
        StartEcho(out var port);

        var response = await _client.GetAsync($"/preview/{port}/");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.GetValues("X-Echo-Auth").Single(),
            Is.EqualTo("none"));
    }

    [Test]
    public async Task Preview_SetCookie_ReescritoParaSubpath()
    {
        UseToken(_user.Token);
        StartEcho(out var port, setCookie: true);

        var response = await _client.GetAsync($"/preview/{port}/app");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.That(cookie, Does.Contain($"Path=/preview/{port}/"));
        // SameSite=Lax sem Secure é removido (RF-002 — iframe same-origin).
        Assert.That(cookie, Does.Not.Contain("SameSite"));
        Assert.That(cookie, Does.Contain("HttpOnly"));
    }

    /// <summary>Shim pra desserializar { enabled } sem depender de record interno.</summary>
    private sealed class JsonElementShim
    {
        public bool Enabled { get; set; }
    }
}

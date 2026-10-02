using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Api.Endpoints;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes dos endpoints OAuth/OIDC e do OAuthService com provedor mockado.</summary>
[TestFixture]
public class OAuthEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;
    private string _adminUserId = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _mockBase = null!;
    private readonly Dictionary<string, string?> _envBackup = new();

    // Respostas mutáveis do provedor mockado (redefinidas a cada [SetUp]).
    private int _tokenStatus;
    private string _tokenBody = "";
    private int _userInfoStatus;
    private string _userInfoBody = "";
    private int _ghUserStatus;
    private string _ghUserBody = "";
    private int _emailsStatus;
    private string _emailsBody = "";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _mock = StartMock();
        _mockBase = _mock.Prefixes.First().TrimEnd('/');
        _mockCts = new CancellationTokenSource();
        _ = Task.Run(() => MockLoopAsync(_mockCts.Token));

        // Provider OIDC genérico permite apontar authorize/token/userinfo para o mock.
        SetEnv("OPENID_PROVIDER_URL", _mockBase);
        SetEnv("OPENID_CLIENT_ID", "test-oidc-client");
        SetEnv("OPENID_CLIENT_SECRET", "test-oidc-secret");
        // GitHub usa URLs fixas do catálogo — só o login (redirect) é testável via HTTP.
        SetEnv("GITHUB_CLIENT_ID", "test-gh-client");
        SetEnv("GITHUB_CLIENT_SECRET", "test-gh-secret");

        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-oauth-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var admin = await SignUpAsync("Admin", "admin@oauth.local", "senha123");
        _adminToken = admin.Token;
        _adminUserId = admin.User.Id;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mockCts.Cancel();
        _mock.Stop();
        _mock.Close();
        _mockCts.Dispose();
        _client.Dispose();
        _factory.Dispose();
        foreach (var (name, value) in _envBackup)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [SetUp]
    public void ResetMock()
    {
        _tokenStatus = 200;
        _tokenBody = """{"access_token":"mock-access","token_type":"Bearer"}""";
        _userInfoStatus = 200;
        _userInfoBody = """{"sub":"sub-padrao","email":"padrao@oauth.local","name":"Padrao"}""";
        _ghUserStatus = 200;
        _ghUserBody = """{"id":4242,"login":"octo","email":null,"name":null}""";
        _emailsStatus = 200;
        _emailsBody = """[{"email":"gh@secret.dev","primary":true,"verified":true}]""";
    }

    private void SetEnv(string name, string value)
    {
        if (!_envBackup.ContainsKey(name))
        {
            _envBackup[name] = Environment.GetEnvironmentVariable(name);
        }
        Environment.SetEnvironmentVariable(name, value);
    }

    private HttpListener StartMock()
    {
        var random = new Random();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var port = random.Next(40000, 60000);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }
        throw new InvalidOperationException("Nenhuma porta livre para o mock OAuth.");
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
            catch (ObjectDisposedException)
            {
                return;
            }

            var path = ctx.Request.Url!.AbsolutePath;
            var (status, json) = path switch
            {
                "/oauth/token" => (_tokenStatus, _tokenBody),
                "/userinfo" => (_userInfoStatus, _userInfoBody),
                "/gh-user" => (_ghUserStatus, _ghUserBody),
                "/user/emails" => (_emailsStatus, _emailsBody),
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

    /// <summary>Obtém um state válido emitido pelo login do provider OIDC.</summary>
    private async Task<string> NewStateAsync()
    {
        var login = await _client.GetAsync("/oauth/oidc/login");
        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        var location = login.Headers.Location!.ToString();
        var stateRaw = location.Split("state=")[1].Split('&')[0];
        return Uri.UnescapeDataString(stateRaw);
    }

    private Task<HttpResponseMessage> CallbackAsync(string? code, string? state)
    {
        var query = new StringBuilder("/oauth/oidc/callback?");
        if (code is not null)
        {
            query.Append("code=").Append(Uri.EscapeDataString(code));
        }
        if (state is not null)
        {
            query.Append("&state=").Append(Uri.EscapeDataString(state));
        }
        return _client.GetAsync(query.ToString());
    }

    private async Task<OAuthService> NewServiceContextAsync(string dbPath)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options);
        await DatabaseMigrator.MigrateAsync(db);
        return new OAuthService(db, new ConfigService(db));
    }

    private static string NewServiceDbPath() =>
        Path.Combine(Path.GetTempPath(), $"openwebui-oauthsvc-{Guid.NewGuid():N}.db");

    /// <summary>HttpMessageHandler que reescreve qualquer host para o mock local.</summary>
    private sealed class RewriteToMockHandler(Uri mockBase)
        : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri = new Uri(mockBase, request.RequestUri!.PathAndQuery);
            return base.SendAsync(request, ct);
        }
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private HttpClient MockHttpClient() =>
        new(new RewriteToMockHandler(new Uri(_mockBase)));

    // ---- Login (GET /oauth/{provider}/login) ----

    [Test, Order(1)]
    public async Task Login_ProviderDesconhecido_Retorna404()
    {
        var response = await _client.GetAsync("/oauth/inexistente/login");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Order(2)]
    public async Task Login_ProviderSemCredenciais_Retorna404()
    {
        var response = await _client.GetAsync("/oauth/google/login");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Order(3)]
    public async Task Login_OidcConfigurado_RedirecionaParaAuthorize()
    {
        var response = await _client.GetAsync("/oauth/oidc/login");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        var location = response.Headers.Location!.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(location, Does.StartWith($"{_mockBase}/authorize?"));
            Assert.That(location, Does.Contain("client_id=test-oidc-client"));
            Assert.That(location, Does.Contain("response_type=code"));
            Assert.That(location, Does.Contain("scope=openid"));
            Assert.That(location, Does.Contain("state="));
            Assert.That(location, Does.Contain(
                "redirect_uri=" + Uri.EscapeDataString("http://localhost/oauth/oidc/callback")));
        });
    }

    [Test, Order(4)]
    public async Task Login_ProviderComCasingMaiusculo_Resolve()
    {
        var response = await _client.GetAsync("/oauth/OIDC/login");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(response.Headers.Location!.ToString(),
            Does.StartWith($"{_mockBase}/authorize?"));
    }

    [Test, Order(5)]
    public async Task Login_Github_RedirecionaParaGithubCom()
    {
        var response = await _client.GetAsync("/oauth/github/login");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        var location = response.Headers.Location!.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(location,
                Does.StartWith("https://github.com/login/oauth/authorize?"));
            Assert.That(location, Does.Contain("client_id=test-gh-client"));
            Assert.That(location, Does.Contain("state="));
        });
    }

    // ---- Callback (GET /oauth/{provider}/callback) ----

    [Test, Order(10)]
    public async Task Callback_ProviderDesconhecido_Retorna404()
    {
        var response = await _client.GetAsync("/oauth/foo/callback?code=abc");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Order(11)]
    public async Task Callback_SemCode_Retorna404()
    {
        var response = await _client.GetAsync("/oauth/oidc/callback");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Order(12)]
    public async Task Callback_StateAusente_Retorna400()
    {
        var response = await CallbackAsync("abc", state: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(13)]
    public async Task Callback_StateMalformado_Retorna400()
    {
        var semAssinatura = await CallbackAsync("abc", "nonce-sem-ponto");
        Assert.That(semAssinatura.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var assinaturaErrada = await CallbackAsync("abc", "abc123.0000deadbeef");
        Assert.That(assinaturaErrada.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(14)]
    public async Task Callback_TokenErroHttp_Retorna400()
    {
        _tokenStatus = 500;
        _tokenBody = """{"error":"server_error"}""";

        var response = await CallbackAsync("abc", await NewStateAsync());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(),
            Does.Contain("Falha na autentica"));
    }

    [Test, Order(15)]
    public async Task Callback_TokenCorpoNaoObjeto_Retorna400()
    {
        _tokenBody = "[]";

        var response = await CallbackAsync("abc", await NewStateAsync());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(16)]
    public async Task Callback_TokenSemAccessToken_Retorna400()
    {
        _tokenBody = """{"token_type":"Bearer"}""";

        var response = await CallbackAsync("abc", await NewStateAsync());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(),
            Does.Contain("dados do usu"));
    }

    [Test, Order(17)]
    public async Task Callback_UserinfoErroHttp_Retorna400()
    {
        _userInfoStatus = 502;
        _userInfoBody = "{}";

        var response = await CallbackAsync("abc", await NewStateAsync());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(18)]
    public async Task Callback_UserinfoSemSub_Retorna400()
    {
        _userInfoBody = """{"email":"x@y.z"}""";

        var response = await CallbackAsync("abc", await NewStateAsync());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test, Order(19)]
    public async Task Callback_SubVazioOuEmailInvalido_Retorna400()
    {
        _userInfoBody = """{"sub":"","email":"a@b.c"}""";
        var subVazio = await CallbackAsync("abc", await NewStateAsync());
        Assert.That(subVazio.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        _userInfoBody = """{"sub":"sub-x","email":123}""";
        var emailNaoString = await CallbackAsync("abc", await NewStateAsync());
        Assert.That(emailNaoString.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await emailNaoString.Content.ReadAsStringAsync(),
            Does.Contain("e-mail verificado"));
    }

    [Test, Order(20)]
    public async Task Callback_UsuarioNovo_CriaContaERedirecionaToken()
    {
        _userInfoBody = """{"sub":"sub-novo","email":"novo@oauth.local","name":"Novo Oauth"}""";

        var response = await CallbackAsync("abc", await NewStateAsync());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(response.Headers.Location!.OriginalString,
            Does.StartWith("/?oauth_token="));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.OAuthAccounts.Include(a => a.User)
            .FirstOrDefaultAsync(a => a.ProviderAccountId == "sub-novo");
        Assert.Multiple(() =>
        {
            Assert.That(account, Is.Not.Null);
            Assert.That(account!.User!.Email, Is.EqualTo("novo@oauth.local"));
            Assert.That(account.User.Role, Is.EqualTo("user"));
        });
    }

    [Test, Order(21)]
    public async Task Callback_ContaJaVinculada_RelogaMesmoUsuario()
    {
        _userInfoBody = """{"sub":"sub-novo","email":"novo@oauth.local","name":"Novo Oauth"}""";

        var response = await CallbackAsync("abc", await NewStateAsync());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(response.Headers.Location!.OriginalString,
            Does.StartWith("/?oauth_token="));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.OAuthAccounts
            .CountAsync(a => a.Provider == "oidc" && a.ProviderAccountId == "sub-novo");
        Assert.That(count, Is.EqualTo(1));
    }

    [Test, Order(22)]
    public async Task Callback_EmailJaCadastrado_VinculaUsuarioExistente()
    {
        _userInfoBody = """{"sub":"sub-vinculo","email":"admin@oauth.local","name":"Admin"}""";

        var response = await CallbackAsync("abc", await NewStateAsync());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(response.Headers.Location!.OriginalString,
            Does.StartWith("/?oauth_token="));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.OAuthAccounts
            .FirstOrDefaultAsync(a => a.Provider == "oidc" && a.ProviderAccountId == "sub-vinculo");
        Assert.That(account?.UserId, Is.EqualTo(_adminUserId));
    }

    [Test, Order(23)]
    public async Task Callback_UsuarioPending_RedirecionaErro()
    {
        UseToken(_adminToken);
        var pending = AdminConfig.Default with { DefaultUserRole = "pending" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", pending);
        _client.DefaultRequestHeaders.Authorization = null;
        _userInfoBody = """{"sub":"sub-pend","email":"pend@oauth.local","name":"Pend"}""";

        var response = await CallbackAsync("abc", await NewStateAsync());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(response.Headers.Location!.OriginalString,
            Does.StartWith("/?oauth_error=pending"));

        UseToken(_adminToken);
        var userRole = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", userRole);
        _client.DefaultRequestHeaders.Authorization = null;
    }

    // ---- FetchUserInfoAsync/FetchGithubEmailAsync (GitHub via reflexão + mock) ----

    [Test, Order(30)]
    public async Task Github_Userinfo_UsaIdComoSubEFallbackDeEmail()
    {
        _ghUserBody = """{"id":4242,"login":"octo","email":null,"name":null}""";
        _emailsBody =
            """[{"email":"sec@gh.dev","primary":false,"verified":true},{"email":"pri@gh.dev","primary":true,"verified":true}]""";
        var config = new OAuthProviderConfig(
            "github", "id", "secret", $"{_mockBase}/authorize",
            $"{_mockBase}/oauth/token", $"{_mockBase}/gh-user", "read:user user:email");
        using var doc = JsonDocument.Parse("""{"access_token":"gh-tok"}""");
        using var http = MockHttpClient();
        var factory = new FakeHttpClientFactory(http);

        var method = typeof(OAuthEndpoints).GetMethod(
            "FetchUserInfoAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task<(string Subject, string? Email, string? Name)?>)method.Invoke(
            null, [config, doc.RootElement, factory, CancellationToken.None])!;
        var result = await task;

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Value.Subject, Is.EqualTo("4242"));
            Assert.That(result.Value.Email, Is.EqualTo("pri@gh.dev"));
            Assert.That(result.Value.Name, Is.EqualTo("octo"));
        });
    }

    [Test, Order(31)]
    public async Task Github_EmailFallback_SemPrimarioVerificado_RetornaNull()
    {
        _ghUserBody = """{"id":1,"login":"x","email":null}""";
        _emailsBody =
            """[{"email":"a@b.c","primary":false,"verified":true},{"email":"b@c.d","primary":true,"verified":false}]""";
        var config = new OAuthProviderConfig(
            "github", "id", "secret", $"{_mockBase}/authorize",
            $"{_mockBase}/oauth/token", $"{_mockBase}/gh-user", "read:user user:email");
        using var doc = JsonDocument.Parse("""{"access_token":"gh-tok"}""");
        using var http = MockHttpClient();
        var factory = new FakeHttpClientFactory(http);

        var method = typeof(OAuthEndpoints).GetMethod(
            "FetchUserInfoAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task<(string Subject, string? Email, string? Name)?>)method.Invoke(
            null, [config, doc.RootElement, factory, CancellationToken.None])!;
        var result = await task;

        Assert.Multiple(() =>
        {
            Assert.That(result!.Value.Subject, Is.EqualTo("1"));
            Assert.That(result.Value.Email, Is.Null);
        });
    }

    [Test, Order(32)]
    public async Task Github_EmailFallback_ErroHttp_RetornaNull()
    {
        _emailsStatus = 500;
        _emailsBody = "{}";
        using var http = MockHttpClient();

        var method = typeof(OAuthEndpoints).GetMethod(
            "FetchGithubEmailAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task<string?>)method.Invoke(
            null, [http, "gh-tok", CancellationToken.None])!;
        var email = await task;

        Assert.That(email, Is.Null);
    }

    [Test, Order(33)]
    public async Task ExchangeCode_Sucesso_RetornaJson()
    {
        var config = new OAuthProviderConfig(
            "oidc", "id", "secret", $"{_mockBase}/authorize",
            $"{_mockBase}/oauth/token", $"{_mockBase}/userinfo", "openid");
        using var http = MockHttpClient();
        var factory = new FakeHttpClientFactory(http);

        var method = typeof(OAuthEndpoints).GetMethod(
            "ExchangeCodeAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task<JsonElement?>)method.Invoke(null,
            [config, "code-1", "http://localhost/oauth/oidc/callback",
             factory, CancellationToken.None])!;
        var json = await task;

        Assert.That(json, Is.Not.Null);
        Assert.That(json!.Value.GetProperty("access_token").GetString(),
            Is.EqualTo("mock-access"));
    }

    // ---- JwtSecret / estado ----

    [Test, Order(40)]
    public async Task JwtSecret_SemEntry_RetornaVazio()
    {
        var dbPath = NewServiceDbPath();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options);
        await DatabaseMigrator.MigrateAsync(db);

        var method = typeof(OAuthEndpoints).GetMethod(
            "JwtSecret", BindingFlags.NonPublic | BindingFlags.Static)!;
        var empty = (string)method.Invoke(null, [db])!;

        db.ConfigEntries.Add(new ConfigEntry
        {
            Key = "webui.jwt.secret",
            ValueJson = JsonSerializer.Serialize("segredo-x"),
        });
        await db.SaveChangesAsync();
        var filled = (string)method.Invoke(null, [db])!;

        Assert.Multiple(() =>
        {
            Assert.That(empty, Is.EqualTo(string.Empty));
            Assert.That(filled, Is.EqualTo("segredo-x"));
        });
        File.Delete(dbPath);
    }

    // ---- OAuthService.LinkOrCreateAsync ----

    [Test, Order(50)]
    public async Task Service_ContaExistente_RetornaMesmoUsuarioSemCriar()
    {
        var dbPath = NewServiceDbPath();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options);
        await DatabaseMigrator.MigrateAsync(db);
        var service = new OAuthService(db, new ConfigService(db));

        var first = await service.LinkOrCreateAsync("oidc", "sub-1", "a@b.c", "A");
        var second = await service.LinkOrCreateAsync("oidc", "sub-1", "a@b.c", "A");

        Assert.Multiple(async () =>
        {
            Assert.That(second.UserId, Is.EqualTo(first.UserId));
            Assert.That(second.NewUser, Is.False);
            Assert.That(await db.Users.CountAsync(), Is.EqualTo(1));
            Assert.That(await db.OAuthAccounts.CountAsync(), Is.EqualTo(1));
        });
        File.Delete(dbPath);
    }

    [Test, Order(51)]
    public async Task Service_EmailExistente_VinculaSemCriarUsuario()
    {
        var dbPath = NewServiceDbPath();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options);
        await DatabaseMigrator.MigrateAsync(db);
        var existente = new User
        {
            Name = "Existente", Email = "exist@b.c",
            PasswordHash = "x", Role = UserRoles.User,
        };
        db.Users.Add(existente);
        await db.SaveChangesAsync();
        var service = new OAuthService(db, new ConfigService(db));

        var link = await service.LinkOrCreateAsync(" OIDC ", "sub-9", " exist@b.c ", null);

        Assert.Multiple(async () =>
        {
            Assert.That(link.UserId, Is.EqualTo(existente.Id));
            Assert.That(link.NewUser, Is.False);
            Assert.That(await db.Users.CountAsync(), Is.EqualTo(1));
        });
        var account = await db.OAuthAccounts.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(account.Provider, Is.EqualTo("oidc"));
            Assert.That(account.Email, Is.EqualTo("exist@b.c"));
        });
        File.Delete(dbPath);
    }

    [Test, Order(52)]
    public async Task Service_PrimeiroUsuario_ViraAdmin()
    {
        var dbPath = NewServiceDbPath();
        var service = await NewServiceContextAsync(dbPath);

        var link = await service.LinkOrCreateAsync("oidc", "sub-first", "first@b.c", "  ");

        Assert.That(link.NewUser, Is.True);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options);
        var user = await db.Users.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(user.Role, Is.EqualTo(UserRoles.Admin));
            Assert.That(user.Name, Is.EqualTo("first@b.c")); // name em branco → e-mail
            Assert.That(user.PasswordHash, Is.EqualTo(string.Empty));
        });
        File.Delete(dbPath);
    }

    [Test, Order(53)]
    public async Task Service_PapelDefaultInvalido_ViraPending()
    {
        var dbPath = NewServiceDbPath();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options);
        await DatabaseMigrator.MigrateAsync(db);
        var config = new ConfigService(db);
        await config.SetAsync("admin.config",
            AdminConfig.Default with { DefaultUserRole = "convidado" });
        db.Users.Add(new User
        {
            Name = "Admin", Email = "adm@b.c",
            PasswordHash = "x", Role = UserRoles.Admin,
        });
        await db.SaveChangesAsync();
        var service = new OAuthService(db, config);

        var link = await service.LinkOrCreateAsync("oidc", "sub-p", "p@b.c", "P");

        Assert.That(link.NewUser, Is.True);
        var user = await db.Users.FirstAsync(u => u.Email == "p@b.c");
        Assert.That(user.Role, Is.EqualTo(UserRoles.Pending));
        File.Delete(dbPath);
    }
}

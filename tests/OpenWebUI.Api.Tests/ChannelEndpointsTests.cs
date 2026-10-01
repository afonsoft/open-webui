using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes da slice realtime-channels: CRUD, membros e mensagens.</summary>
[TestFixture]
public class ChannelEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;
    private AuthResponse _user = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-ch-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _admin = await SignUpAsync("Admin", "admin@test.local", "senha123");
        UseToken(_admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        _client.DefaultRequestHeaders.Authorization = null;
        _user = await SignUpAsync("User", "user@test.local", "senha123");
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

    [Test]
    public async Task Channels_CRUD_CriadorViraAdmin() // RF-001
    {
        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/channels",
            new CreateChannelRequest("geral", "Canal geral", [_user.User.Id]));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var channel = (await created.Content.ReadFromJsonAsync<ChannelResponse>())!;
        Assert.That(channel.MyRole, Is.EqualTo("admin"));
        Assert.That(channel.MemberCount, Is.EqualTo(2));

        var list = await _client.GetFromJsonAsync<List<ChannelResponse>>("/api/v1/channels");
        Assert.That(list!.Any(c => c.Id == channel.Id), Is.True);

        var detail = await _client.GetFromJsonAsync<ChannelDetailResponse>(
            $"/api/v1/channels/{channel.Id}");
        Assert.That(detail!.Members.Select(m => m.UserId),
            Is.EquivalentTo(new[] { _admin.User.Id, _user.User.Id }));
    }

    [Test]
    public async Task Channels_NaoMembro_NaoVeCanal() // RF-001
    {
        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/channels",
            new CreateChannelRequest("privado", null, null));
        var channel = (await created.Content.ReadFromJsonAsync<ChannelResponse>())!;

        UseToken(_user.Token);
        var list = await _client.GetFromJsonAsync<List<ChannelResponse>>("/api/v1/channels");
        Assert.That(list!.Any(c => c.Id == channel.Id), Is.False);

        var get = await _client.GetAsync($"/api/v1/channels/{channel.Id}");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task ChannelMessages_EnviarEListar() // RF-002
    {
        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/channels",
            new CreateChannelRequest("chat", null, [_user.User.Id]));
        var channel = (await created.Content.ReadFromJsonAsync<ChannelResponse>())!;

        var posted = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channel.Id}/messages",
            new CreateChannelMessageRequest("Olá canal"));
        Assert.That(posted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        UseToken(_user.Token);
        var messages = await _client.GetFromJsonAsync<List<ChannelMessageResponse>>(
            $"/api/v1/channels/{channel.Id}/messages");
        Assert.That(messages, Has.Count.GreaterThanOrEqualTo(1));
        Assert.That(messages![0].AuthorName, Is.EqualTo("Admin"));
        Assert.That(messages[0].Content, Is.EqualTo("Olá canal"));

        var memberPost = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channel.Id}/messages",
            new CreateChannelMessageRequest("Oi admin"));
        Assert.That(memberPost.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task ChannelMessages_NaoMembro_NaoPosta() // RF-002
    {
        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/channels",
            new CreateChannelRequest("restrito", null, null));
        var channel = (await created.Content.ReadFromJsonAsync<ChannelResponse>())!;

        UseToken(_user.Token);
        var post = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channel.Id}/messages",
            new CreateChannelMessageRequest("não sou membro"));
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task ChannelMessages_MencaoModelo_SemProvedor_PostaErro() // RF-004
    {
        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/channels",
            new CreateChannelRequest("ia", null, null));
        var channel = (await created.Content.ReadFromJsonAsync<ChannelResponse>())!;

        await _client.PostAsJsonAsync($"/api/v1/channels/{channel.Id}/messages",
            new CreateChannelMessageRequest("@modelo-inexistente oi"));

        List<ChannelMessageResponse>? messages = null;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(300);
            messages = await _client.GetFromJsonAsync<List<ChannelMessageResponse>>(
                $"/api/v1/channels/{channel.Id}/messages");
            if (messages!.Count >= 2)
            {
                break;
            }
        }
        Assert.That(messages, Has.Count.GreaterThanOrEqualTo(2));
        var reply = messages![1];
        Assert.That(reply.UserId, Is.Null);
        Assert.That(reply.Content, Does.Contain("não encontrado"));
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name, email, password });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
}

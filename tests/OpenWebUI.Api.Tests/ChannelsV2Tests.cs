using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes da slice channels-v2: DMs, threads, reações, lidos e pins.</summary>
[TestFixture, IsolateEnvironment]
public class ChannelsV2Tests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;
    private AuthResponse _user = null!;
    private AuthResponse _third = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-chv2-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _admin = await SignUpAsync("Admin", "admin-chv2@test.local", "senha123");
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        _client.DefaultRequestHeaders.Authorization = null;
        _user = await SignUpAsync("User", "user-chv2@test.local", "senha123");
        _third = await SignUpAsync("Third", "third-chv2@test.local", "senha123");
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
        var response = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name, email, password });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    [Test]
    public async Task Dm_CreateOrReturn_DoisMembros() // RF-001
    {
        UseToken(_user.Token);
        var first = await _client.PostAsJsonAsync("/api/v1/channels/dm",
            new CreateDmRequest(_admin.User.Id));
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var dm = (await first.Content.ReadFromJsonAsync<ChannelResponse>())!;
        Assert.That(dm.Type, Is.EqualTo("dm"));
        Assert.That(dm.MemberCount, Is.EqualTo(2));
        Assert.That(dm.Name, Is.EqualTo("Admin"));

        // chamar de novo retorna o mesmo DM
        var second = await _client.PostAsJsonAsync("/api/v1/channels/dm",
            new CreateDmRequest(_admin.User.Id));
        var dm2 = (await second.Content.ReadFromJsonAsync<ChannelResponse>())!;
        Assert.That(dm2.Id, Is.EqualTo(dm.Id));

        // para o admin o nome do DM é o outro membro
        UseToken(_admin.Token);
        var adminList = await _client.GetFromJsonAsync<List<ChannelResponse>>("/api/v1/channels");
        Assert.That(adminList!.First(c => c.Id == dm.Id).Name, Is.EqualTo("User"));
    }

    [Test]
    public async Task Dm_TerceiroNaoVe_NaoSaiNaoAdiciona() // RF-001
    {
        var list = await GetDmIdAsync();
        UseToken(_third.Token);
        var channels = await _client.GetFromJsonAsync<List<ChannelResponse>>("/api/v1/channels");
        Assert.That(channels!.Any(c => c.Id == list), Is.False);
        var detail = await _client.GetAsync($"/api/v1/channels/{list}");
        Assert.That(detail.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // ninguém sai nem adiciona membro em DM
        UseToken(_user.Token);
        var leave = await _client.DeleteAsync($"/api/v1/channels/{list}/members/{_user.User.Id}");
        Assert.That(leave.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var add = await _client.PostAsJsonAsync($"/api/v1/channels/{list}/members",
            new AddChannelMembersRequest([_third.User.Id], null));
        Assert.That(add.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    private async Task<string> GetDmIdAsync()
    {
        UseToken(_user.Token);
        var channels = await _client.GetFromJsonAsync<List<ChannelResponse>>("/api/v1/channels");
        return channels!.First(c => c.Type == "dm").Id;
    }

    [Test]
    public async Task Threads_RespostasEReplyCount() // RF-002
    {
        var channelId = await GetDmIdAsync();
        UseToken(_user.Token);
        var post = await _client.PostAsJsonAsync($"/api/v1/channels/{channelId}/messages",
            new CreateChannelMessageRequest("Mensagem raiz", null));
        var root = (await post.Content.ReadFromJsonAsync<ChannelMessageResponse>())!;
        Assert.That(root.ParentId, Is.Null);

        UseToken(_admin.Token);
        var reply = await _client.PostAsJsonAsync($"/api/v1/channels/{channelId}/messages",
            new CreateChannelMessageRequest("Resposta em thread", root.Id));
        reply.EnsureSuccessStatusCode();
        var replyMsg = (await reply.Content.ReadFromJsonAsync<ChannelMessageResponse>())!;
        Assert.That(replyMsg.ParentId, Is.EqualTo(root.Id));

        var replies = await _client.GetFromJsonAsync<List<ChannelMessageResponse>>(
            $"/api/v1/channels/{channelId}/messages/{root.Id}/replies");
        Assert.That(replies!.Select(r => r.Id), Is.EqualTo(new[] { replyMsg.Id }));

        // feed principal só traz raízes, com reply_count
        var feed = await _client.GetFromJsonAsync<List<ChannelMessageResponse>>(
            $"/api/v1/channels/{channelId}/messages");
        Assert.That(feed!.Any(m => m.Id == replyMsg.Id), Is.False);
        Assert.That(feed!.First(m => m.Id == root.Id).ReplyCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Reactions_ToggleEAggregado() // RF-003
    {
        var channelId = await GetDmIdAsync();
        UseToken(_user.Token);
        var post = await _client.PostAsJsonAsync($"/api/v1/channels/{channelId}/messages",
            new CreateChannelMessageRequest("Reaja a mim", null));
        var msg = (await post.Content.ReadFromJsonAsync<ChannelMessageResponse>())!;

        var added = await _client.PostAsync(
            $"/api/v1/channels/{channelId}/messages/{msg.Id}/reactions/%F0%9F%91%8D", null);
        added.EnsureSuccessStatusCode();
        var agg = (await added.Content.ReadFromJsonAsync<List<ChannelReactionResponse>>())!;
        Assert.That(agg[0].Emoji, Is.EqualTo("👍"));
        Assert.That(agg[0].Count, Is.EqualTo(1));
        Assert.That(agg[0].UserIds, Contains.Item(_user.User.Id));

        // admin também reage → count 2
        UseToken(_admin.Token);
        var added2 = await _client.PostAsync(
            $"/api/v1/channels/{channelId}/messages/{msg.Id}/reactions/%F0%9F%91%8D", null);
        var agg2 = (await added2.Content.ReadFromJsonAsync<List<ChannelReactionResponse>>())!;
        Assert.That(agg2[0].Count, Is.EqualTo(2));

        // toggle: user remove a própria reação
        UseToken(_user.Token);
        var removed = await _client.DeleteAsync(
            $"/api/v1/channels/{channelId}/messages/{msg.Id}/reactions/%F0%9F%91%8D");
        var agg3 = (await removed.Content.ReadFromJsonAsync<List<ChannelReactionResponse>>())!;
        Assert.That(agg3[0].Count, Is.EqualTo(1));
        Assert.That(agg3[0].UserIds, Does.Not.Contain(_user.User.Id));

        // feed traz o agregado
        var feed = await _client.GetFromJsonAsync<List<ChannelMessageResponse>>(
            $"/api/v1/channels/{channelId}/messages");
        Assert.That(feed!.First(m => m.Id == msg.Id).Reactions[0].Count, Is.EqualTo(1));
    }

    [Test]
    public async Task Read_UnreadCount_MarcaLido() // RF-004
    {
        // canal de grupo entre admin e user
        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/channels",
            new CreateChannelRequest("leituras", null, [_user.User.Id]));
        var channel = (await created.Content.ReadFromJsonAsync<ChannelResponse>())!;

        // user não leu nada ainda → unread = 0 (nenhuma mensagem)
        UseToken(_user.Token);
        var before = await _client.GetFromJsonAsync<List<ChannelResponse>>("/api/v1/channels");
        Assert.That(before!.First(c => c.Id == channel.Id).UnreadCount, Is.EqualTo(0));

        // admin posta 2 mensagens
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync($"/api/v1/channels/{channel.Id}/messages",
            new CreateChannelMessageRequest("uma", null));
        await _client.PostAsJsonAsync($"/api/v1/channels/{channel.Id}/messages",
            new CreateChannelMessageRequest("duas", null));

        UseToken(_user.Token);
        var unread = await _client.GetFromJsonAsync<List<ChannelResponse>>("/api/v1/channels");
        Assert.That(unread!.First(c => c.Id == channel.Id).UnreadCount, Is.EqualTo(2));

        var read = await _client.PostAsync($"/api/v1/channels/{channel.Id}/read", null);
        read.EnsureSuccessStatusCode();
        var after = await _client.GetFromJsonAsync<List<ChannelResponse>>("/api/v1/channels");
        Assert.That(after!.First(c => c.Id == channel.Id).UnreadCount, Is.EqualTo(0));

        // próprias mensagens do user não contam para ele
        await _client.PostAsJsonAsync($"/api/v1/channels/{channel.Id}/messages",
            new CreateChannelMessageRequest("minha", null));
        var self = await _client.GetFromJsonAsync<List<ChannelResponse>>("/api/v1/channels");
        Assert.That(self!.First(c => c.Id == channel.Id).UnreadCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Pin_FixaELista() // RF-005
    {
        var channelId = await GetDmIdAsync();
        UseToken(_user.Token);
        var post = await _client.PostAsJsonAsync($"/api/v1/channels/{channelId}/messages",
            new CreateChannelMessageRequest("Fixe-me", null));
        var msg = (await post.Content.ReadFromJsonAsync<ChannelMessageResponse>())!;

        var pin = await _client.PostAsync(
            $"/api/v1/channels/{channelId}/messages/{msg.Id}/pin", null);
        pin.EnsureSuccessStatusCode();

        var pinned = await _client.GetFromJsonAsync<List<ChannelMessageResponse>>(
            $"/api/v1/channels/{channelId}/pinned");
        Assert.That(pinned!.Select(p => p.Id), Is.EqualTo(new[] { msg.Id }));
        Assert.That(pinned![0].IsPinned, Is.True);

        var unpin = await _client.DeleteAsync(
            $"/api/v1/channels/{channelId}/messages/{msg.Id}/pin");
        unpin.EnsureSuccessStatusCode();
        var after = await _client.GetFromJsonAsync<List<ChannelMessageResponse>>(
            $"/api/v1/channels/{channelId}/pinned");
        Assert.That(after!, Is.Empty);
    }

    [Test]
    public async Task Reactions_NaoMembro_Forbidden() // RF-003
    {
        var channelId = await GetDmIdAsync();
        UseToken(_user.Token);
        var post = await _client.PostAsJsonAsync($"/api/v1/channels/{channelId}/messages",
            new CreateChannelMessageRequest("Msg", null));
        var msg = (await post.Content.ReadFromJsonAsync<ChannelMessageResponse>())!;

        UseToken(_third.Token);
        var react = await _client.PostAsync(
            $"/api/v1/channels/{channelId}/messages/{msg.Id}/reactions/%F0%9F%91%8D", null);
        Assert.That(react.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}

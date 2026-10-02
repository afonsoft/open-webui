using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes de update/delete, gestão de membros e menção @modelo nos canais.</summary>
[TestFixture]
public class ChannelMembersTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openwebui-chmem-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("Admin", "admin@chmem.local", "senha123");
        UseToken(_admin.Token);
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
            File.Delete(_dbPath);
        }
    }

    [Test, Order(1)]
    public async Task UpdateChannel_OwnerAtualiza_MembroComumForbid_NaoMembroNotFound()
    {
        var owner = await SignUpAsync("Owner1", "owner1@chmem.local", "senha123");
        var member = await SignUpAsync("Member1", "member1@chmem.local", "senha123");
        var outsider = await SignUpAsync("Out1", "out1@chmem.local", "senha123");

        // Owner é usuário comum global, mas criador vira admin do canal.
        UseToken(owner.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/channels",
            new CreateChannelRequest("editavel", "desc original", [member.User.Id]));
        var channel = (await created.Content.ReadFromJsonAsync<ChannelResponse>())!;
        Assert.That(channel.MyRole, Is.EqualTo("admin"));

        // Membro comum (papel "member") não pode atualizar → 403.
        UseToken(member.Token);
        var memberPut = await _client.PutAsJsonAsync($"/api/v1/channels/{channel.Id}",
            new UpdateChannelRequest("hackeado", null));
        Assert.That(memberPut.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        // Não-membro → 404 (canal invisível).
        UseToken(outsider.Token);
        var outPut = await _client.PutAsJsonAsync($"/api/v1/channels/{channel.Id}",
            new UpdateChannelRequest("hackeado", null));
        Assert.That(outPut.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Owner (admin do canal, não global) atualiza nome e descrição.
        UseToken(owner.Token);
        var okPut = await _client.PutAsJsonAsync($"/api/v1/channels/{channel.Id}",
            new UpdateChannelRequest("renomeado", "nova desc"));
        Assert.That(okPut.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var detail = await _client.GetFromJsonAsync<ChannelDetailResponse>(
            $"/api/v1/channels/{channel.Id}");
        Assert.Multiple(() =>
        {
            Assert.That(detail!.Name, Is.EqualTo("renomeado"));
            Assert.That(detail.Description, Is.EqualTo("nova desc"));
        });

        // Nome em branco é ignorado: mantém o atual.
        var keepName = await _client.PutAsJsonAsync($"/api/v1/channels/{channel.Id}",
            new UpdateChannelRequest("   ", "só desc"));
        Assert.That(keepName.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var detail2 = await _client.GetFromJsonAsync<ChannelDetailResponse>(
            $"/api/v1/channels/{channel.Id}");
        Assert.Multiple(() =>
        {
            Assert.That(detail2!.Name, Is.EqualTo("renomeado"));
            Assert.That(detail2.Description, Is.EqualTo("só desc"));
        });
    }

    [Test, Order(2)]
    public async Task DeleteChannel_SomenteAdminDoCanal_Remove()
    {
        var owner = await SignUpAsync("Owner2", "owner2@chmem.local", "senha123");
        var member = await SignUpAsync("Member2", "member2@chmem.local", "senha123");

        UseToken(owner.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/channels",
            new CreateChannelRequest("deletavel", null, [member.User.Id]));
        var channel = (await created.Content.ReadFromJsonAsync<ChannelResponse>())!;

        // Membro comum não pode deletar → 403.
        UseToken(member.Token);
        var memberDel = await _client.DeleteAsync($"/api/v1/channels/{channel.Id}");
        Assert.That(memberDel.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        // Admin global que NÃO é membro também recebe 404.
        UseToken(_admin.Token);
        var adminDel = await _client.DeleteAsync($"/api/v1/channels/{channel.Id}");
        Assert.That(adminDel.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Owner (admin do canal) deleta → 200 e canal some para todos.
        UseToken(owner.Token);
        var okDel = await _client.DeleteAsync($"/api/v1/channels/{channel.Id}");
        Assert.That(okDel.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var gone = await _client.GetAsync($"/api/v1/channels/{channel.Id}");
        Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Deletar canal inexistente → 404.
        var delAgain = await _client.DeleteAsync($"/api/v1/channels/{channel.Id}");
        Assert.That(delAgain.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, Order(3)]
    public async Task AddMembers_AdminAdicionaComPapel_Idempotente()
    {
        var owner = await SignUpAsync("Owner3", "owner3@chmem.local", "senha123");
        var member = await SignUpAsync("Member3", "member3@chmem.local", "senha123");
        var extra = await SignUpAsync("Extra3", "extra3@chmem.local", "senha123");
        var outsider = await SignUpAsync("Out3", "out3@chmem.local", "senha123");

        UseToken(owner.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/channels",
            new CreateChannelRequest("membros", null, [member.User.Id]));
        var channel = (await created.Content.ReadFromJsonAsync<ChannelResponse>())!;

        // Membro comum não pode adicionar → 403.
        UseToken(member.Token);
        var memberAdd = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channel.Id}/members",
            new AddChannelMembersRequest([extra.User.Id], null));
        Assert.That(memberAdd.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        // Não-membro → 404.
        UseToken(outsider.Token);
        var outAdd = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channel.Id}/members",
            new AddChannelMembersRequest([extra.User.Id], null));
        Assert.That(outAdd.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Owner adiciona com papel admin; ids duplicados/inexistentes são ignorados.
        UseToken(owner.Token);
        var add = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channel.Id}/members",
            new AddChannelMembersRequest(
                [extra.User.Id, extra.User.Id, "usuario-inexistente", member.User.Id], "admin"));
        Assert.That(add.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var detail = await _client.GetFromJsonAsync<ChannelDetailResponse>(
            $"/api/v1/channels/{channel.Id}");
        Assert.That(detail!.Members, Has.Count.EqualTo(3));
        var extraMember = detail.Members.First(m => m.UserId == extra.User.Id);
        var normalMember = detail.Members.First(m => m.UserId == member.User.Id);
        Assert.Multiple(() =>
        {
            Assert.That(extraMember.Role, Is.EqualTo("admin"));
            Assert.That(normalMember.Role, Is.EqualTo("member"));
        });

        // Extra agora é admin do canal e consegue adicionar o outsider.
        UseToken(extra.Token);
        var extraAdd = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channel.Id}/members",
            new AddChannelMembersRequest([outsider.User.Id], "member"));
        Assert.That(extraAdd.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test, Order(4)]
    public async Task RemoveMember_AdminRemove_MembroSoSeRemove()
    {
        var owner = await SignUpAsync("Owner4", "owner4@chmem.local", "senha123");
        var member = await SignUpAsync("Member4", "member4@chmem.local", "senha123");
        var extra = await SignUpAsync("Extra4", "extra4@chmem.local", "senha123");

        UseToken(owner.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/channels",
            new CreateChannelRequest("saída", null, [member.User.Id, extra.User.Id]));
        var channel = (await created.Content.ReadFromJsonAsync<ChannelResponse>())!;

        // Membro comum não pode remover outro → 403.
        UseToken(member.Token);
        var memberDel = await _client.DeleteAsync(
            $"/api/v1/channels/{channel.Id}/members/{extra.User.Id}");
        Assert.That(memberDel.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        // Membro pode sair do canal (remover a si mesmo).
        var selfDel = await _client.DeleteAsync(
            $"/api/v1/channels/{channel.Id}/members/{member.User.Id}");
        Assert.That(selfDel.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var goneForMember = await _client.GetAsync($"/api/v1/channels/{channel.Id}");
        Assert.That(goneForMember.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Admin do canal remove o extra → 200.
        UseToken(owner.Token);
        var adminDel = await _client.DeleteAsync(
            $"/api/v1/channels/{channel.Id}/members/{extra.User.Id}");
        Assert.That(adminDel.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Remover quem não é membro → 404 "Membro não encontrado".
        var delAgain = await _client.DeleteAsync(
            $"/api/v1/channels/{channel.Id}/members/{extra.User.Id}");
        Assert.That(delAgain.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var detail = await _client.GetFromJsonAsync<ChannelDetailResponse>(
            $"/api/v1/channels/{channel.Id}");
        Assert.That(detail!.Members.Select(m => m.UserId),
            Is.EquivalentTo(new[] { owner.User.Id }));
    }

    [Test, Order(5)]
    public async Task PostMessage_ValidacaoEConteudo()
    {
        var owner = await SignUpAsync("Owner5", "owner5@chmem.local", "senha123");
        UseToken(owner.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/channels",
            new CreateChannelRequest("mensagens", null, null));
        var channel = (await created.Content.ReadFromJsonAsync<ChannelResponse>())!;

        // Conteúdo vazio/em branco → 400.
        var empty = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channel.Id}/messages",
            new CreateChannelMessageRequest("   "));
        Assert.That(empty.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Post válido retorna a mensagem com autor.
        var posted = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channel.Id}/messages",
            new CreateChannelMessageRequest("Primeira mensagem"));
        Assert.That(posted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var message = (await posted.Content.ReadFromJsonAsync<ChannelMessageResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(message.UserId, Is.EqualTo(owner.User.Id));
            Assert.That(message.AuthorName, Is.EqualTo("Owner5"));
            Assert.That(message.Content, Is.EqualTo("Primeira mensagem"));
        });

        // Paginação: skip/take são respeitados.
        await _client.PostAsJsonAsync($"/api/v1/channels/{channel.Id}/messages",
            new CreateChannelMessageRequest("Segunda mensagem"));
        var page = await _client.GetFromJsonAsync<List<ChannelMessageResponse>>(
            $"/api/v1/channels/{channel.Id}/messages?skip=1&take=1");
        Assert.That(page, Has.Count.EqualTo(1));
        Assert.That(page![0].Content, Is.EqualTo("Segunda mensagem"));
    }

    [Test, Order(6)]
    public async Task PostMessage_MencaoModelo_RespondeComProvedorMock()
    {
        var owner = await SignUpAsync("Owner6", "owner6@chmem.local", "senha123");
        UseToken(owner.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/channels",
            new CreateChannelRequest("ia-mock", null, null));
        var channel = (await created.Content.ReadFromJsonAsync<ChannelResponse>())!;

        var posted = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channel.Id}/messages",
            new CreateChannelMessageRequest("@fake:1 qual é a resposta?"));
        Assert.That(posted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // ReplyWithModelAsync roda em background: aguarda a resposta do mock.
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
        Assert.Multiple(() =>
        {
            Assert.That(reply.UserId, Is.Null);
            Assert.That(reply.ModelId, Is.EqualTo("fake:1"));
            Assert.That(reply.AuthorName, Is.EqualTo("fake:1"));
            Assert.That(reply.Content, Is.EqualTo("resposta do mock"));
        });
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
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}

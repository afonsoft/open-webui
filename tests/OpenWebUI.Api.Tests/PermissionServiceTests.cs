using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes diretos do PermissionService (união de permissões por grupos) sobre SQLite.</summary>
[TestFixture, IsolateEnvironment]
public class PermissionServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private string _dbPath = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-permissions-{Guid.NewGuid():N}.db");
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private AppDbContext CreateContext(bool enforceForeignKeys = true)
    {
        var dataSource = $"Data Source={_dbPath}";
        if (!enforceForeignKeys)
        {
            dataSource += ";Foreign Keys=False";
        }
        return new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(dataSource).Options);
    }

    private static string Json(GroupPermissions permissions) =>
        JsonSerializer.Serialize(permissions, JsonOptions);

    /// <summary>Monta permissões completas negando apenas a flag indicada.</summary>
    private static GroupPermissions Negando(string permission) =>
        new(
            new WorkspacePermissions(
                Models: permission != PermissionService.WorkspaceModels,
                Prompts: permission != PermissionService.WorkspacePrompts,
                Knowledge: permission != PermissionService.WorkspaceKnowledge,
                Tools: permission != PermissionService.WorkspaceTools,
                Files: permission != PermissionService.WorkspaceFiles),
            new SharingPermissions(PublicChats: permission != PermissionService.SharingPublicChats),
            new ChatPermissions(Controls: permission != PermissionService.ChatControls));

    private static User NovoUsuario(string id, string role) =>
        new() { Id = id, Name = id, Email = $"{id}@test.local", PasswordHash = "x", Role = role };

    private async Task SeedAsync(params object[] entidades)
    {
        await using var db = CreateContext();
        db.AddRange(entidades);
        await db.SaveChangesAsync();
    }

    private async Task<bool> HasAsync(User user, string permission, CancellationToken ct = default)
    {
        await using var db = CreateContext();
        return await new PermissionService(db).HasAsync(user, permission, ct);
    }

    [Test]
    public async Task Admin_SemGrupos_TemTodasPermissoes()
    {
        // Branch: user.Role == Admin → retorno imediato sem consultar grupos.
        var admin = NovoUsuario("u-admin", UserRoles.Admin);

        Assert.That(await HasAsync(admin, PermissionService.WorkspaceModels), Is.True);
    }

    [Test]
    public async Task Admin_MembroDeGrupoRestritivo_MantemTodasPermissoes()
    {
        // Branch: admin bypassa mesmo quando o grupo nega todas as flags.
        var admin = NovoUsuario("u-admin", UserRoles.Admin);
        var grupo = new Group { Id = "g1", Name = "restrito", PermissionsJson = Json(Negando("qualquer")) };
        var membro = new GroupMember { GroupId = "g1", UserId = "u-admin", Role = "member" };
        await SeedAsync(admin, grupo, membro);

        Assert.Multiple(async () =>
        {
            Assert.That(await HasAsync(admin, PermissionService.WorkspaceModels), Is.True);
            Assert.That(await HasAsync(admin, PermissionService.ChatControls), Is.True);
        });
    }

    [Test]
    public async Task Usuario_SemGrupos_DefaultTrue()
    {
        // Branch: groups.Count == 0 → true (comportamento default preservado).
        var usuario = NovoUsuario("u1", UserRoles.User);
        await SeedAsync(usuario);

        Assert.That(await HasAsync(usuario, PermissionService.WorkspaceFiles), Is.True);
    }

    [Test]
    public async Task Usuario_Inexistente_SemMemberships_DefaultTrue()
    {
        // Branch: usuário não persistido → nenhum GroupMember → count 0 → true.
        var fantasma = NovoUsuario("nao-existe", UserRoles.User);

        Assert.That(await HasAsync(fantasma, PermissionService.SharingPublicChats), Is.True);
    }

    [Test]
    public async Task Membro_GrupoCompleto_RetornaTrue()
    {
        // Branch: groups.Any(Applies) com JSON concedendo tudo.
        var usuario = NovoUsuario("u1", UserRoles.User);
        var grupo = new Group { Id = "g1", Name = "completo", PermissionsJson = Json(GroupPermissions.Full) };
        var membro = new GroupMember { GroupId = "g1", UserId = "u1" };
        await SeedAsync(usuario, grupo, membro);

        Assert.Multiple(async () =>
        {
            Assert.That(await HasAsync(usuario, PermissionService.WorkspaceKnowledge), Is.True);
            Assert.That(await HasAsync(usuario, PermissionService.SharingPublicChats), Is.True);
        });
    }

    [Test]
    [TestCase(PermissionService.WorkspaceModels)]
    [TestCase(PermissionService.WorkspacePrompts)]
    [TestCase(PermissionService.WorkspaceKnowledge)]
    [TestCase(PermissionService.WorkspaceTools)]
    [TestCase(PermissionService.WorkspaceFiles)]
    [TestCase(PermissionService.SharingPublicChats)]
    [TestCase(PermissionService.ChatControls)]
    public async Task Membro_GrupoNegandoFlag_RetornaFalse(string permission)
    {
        // Branch: cada braço do switch retorna a flag false do JSON.
        var usuario = NovoUsuario("u1", UserRoles.User);
        var grupo = new Group { Id = "g1", Name = "nega-" + permission, PermissionsJson = Json(Negando(permission)) };
        var membro = new GroupMember { GroupId = "g1", UserId = "u1" };
        await SeedAsync(usuario, grupo, membro);

        Assert.That(await HasAsync(usuario, permission), Is.False);
    }

    [Test]
    [TestCase(PermissionService.WorkspaceModels)]
    [TestCase(PermissionService.WorkspacePrompts)]
    [TestCase(PermissionService.WorkspaceKnowledge)]
    [TestCase(PermissionService.WorkspaceTools)]
    [TestCase(PermissionService.WorkspaceFiles)]
    [TestCase(PermissionService.SharingPublicChats)]
    [TestCase(PermissionService.ChatControls)]
    public async Task Membro_GrupoNegandoOutraFlag_PermissionSolicitadaTrue(string permission)
    {
        // Branch: braço do switch retorna flag true quando a negação é de outra permissão.
        var usuario = NovoUsuario("u1", UserRoles.User);
        var outra = permission == PermissionService.ChatControls
            ? PermissionService.WorkspaceModels
            : PermissionService.ChatControls;
        var grupo = new Group { Id = "g1", Name = "nega-outra-" + permission, PermissionsJson = Json(Negando(outra)) };
        var membro = new GroupMember { GroupId = "g1", UserId = "u1" };
        await SeedAsync(usuario, grupo, membro);

        Assert.That(await HasAsync(usuario, permission), Is.True);
    }

    [Test]
    public async Task Membro_GruposMultiplos_UniaoConcede()
    {
        // Branch: união — um grupo nega, outro concede → Any retorna true.
        var usuario = NovoUsuario("u1", UserRoles.User);
        var restrito = new Group { Id = "g1", Name = "restrito", PermissionsJson = Json(Negando(PermissionService.WorkspaceModels)) };
        var liberado = new Group { Id = "g2", Name = "liberado", PermissionsJson = Json(GroupPermissions.Full) };
        await SeedAsync(usuario, restrito, liberado,
            new GroupMember { GroupId = "g1", UserId = "u1" },
            new GroupMember { GroupId = "g2", UserId = "u1" });

        Assert.That(await HasAsync(usuario, PermissionService.WorkspaceModels), Is.True);
    }

    [Test]
    public async Task Membro_GruposMultiplos_TodosNegam_RetornaFalse()
    {
        // Branch: união — todos os grupos negam → Any retorna false.
        var usuario = NovoUsuario("u1", UserRoles.User);
        var g1 = new Group { Id = "g1", Name = "nega1", PermissionsJson = Json(Negando(PermissionService.WorkspaceTools)) };
        var g2 = new Group { Id = "g2", Name = "nega2", PermissionsJson = Json(Negando(PermissionService.WorkspaceTools)) };
        await SeedAsync(usuario, g1, g2,
            new GroupMember { GroupId = "g1", UserId = "u1" },
            new GroupMember { GroupId = "g2", UserId = "u1" });

        Assert.Multiple(async () =>
        {
            Assert.That(await HasAsync(usuario, PermissionService.WorkspaceTools), Is.False);
            Assert.That(await HasAsync(usuario, PermissionService.WorkspaceFiles), Is.True);
        });
    }

    [Test]
    [TestCase("{json invalido")]
    [TestCase("")]
    [TestCase("null")]
    public async Task Membro_GrupoJsonQuebrado_Nega(string permissionsJson)
    {
        // Branch: JsonException no catch (inválido/vazio) e deserialize null ("null") → false.
        var usuario = NovoUsuario("u1", UserRoles.User);
        var grupo = new Group { Id = "g1", Name = "quebrado-" + permissionsJson.GetHashCode(), PermissionsJson = permissionsJson };
        var membro = new GroupMember { GroupId = "g1", UserId = "u1" };
        await SeedAsync(usuario, grupo, membro);

        Assert.That(await HasAsync(usuario, PermissionService.WorkspacePrompts), Is.False);
    }

    [Test]
    public async Task Membro_GrupoJsonQuebrado_ComOutroGrupoValido_UniaoTrue()
    {
        // Branch: JsonException em um grupo não impede a concessão por outro.
        var usuario = NovoUsuario("u1", UserRoles.User);
        var quebrado = new Group { Id = "g1", Name = "quebrado", PermissionsJson = "{" };
        var valido = new Group { Id = "g2", Name = "valido", PermissionsJson = Json(GroupPermissions.Full) };
        await SeedAsync(usuario, quebrado, valido,
            new GroupMember { GroupId = "g1", UserId = "u1" },
            new GroupMember { GroupId = "g2", UserId = "u1" });

        Assert.That(await HasAsync(usuario, PermissionService.WorkspacePrompts), Is.True);
    }

    [Test]
    public async Task Membro_GrupoJsonObjetoVazio_DefaultsTrue()
    {
        // Branch: "{}" → seções null substituídas por defaults (todas as flags true).
        var usuario = NovoUsuario("u1", UserRoles.User);
        var grupo = new Group { Id = "g1", Name = "vazio", PermissionsJson = "{}" };
        var membro = new GroupMember { GroupId = "g1", UserId = "u1" };
        await SeedAsync(usuario, grupo, membro);

        Assert.Multiple(async () =>
        {
            Assert.That(await HasAsync(usuario, PermissionService.WorkspaceModels), Is.True);
            Assert.That(await HasAsync(usuario, PermissionService.SharingPublicChats), Is.True);
            Assert.That(await HasAsync(usuario, PermissionService.ChatControls), Is.True);
        });
    }

    [Test]
    public async Task Membro_GrupoJsonSecoesNulas_MisturaDefaultsComNegacao()
    {
        // Branch: workspace preenchido com negação; sharing/chat null → defaults true.
        var usuario = NovoUsuario("u1", UserRoles.User);
        var permissoes = new GroupPermissions(
            new WorkspacePermissions(Models: false), Sharing: null, Chat: null);
        var grupo = new Group { Id = "g1", Name = "parcial", PermissionsJson = Json(permissoes) };
        var membro = new GroupMember { GroupId = "g1", UserId = "u1" };
        await SeedAsync(usuario, grupo, membro);

        Assert.Multiple(async () =>
        {
            Assert.That(await HasAsync(usuario, PermissionService.WorkspaceModels), Is.False);
            Assert.That(await HasAsync(usuario, PermissionService.SharingPublicChats), Is.True);
            Assert.That(await HasAsync(usuario, PermissionService.ChatControls), Is.True);
        });
    }

    [Test]
    public async Task Membro_RoleAdminDentroDoGrupo_NaoElevaPermissoes()
    {
        // Branch: papel "admin" no vínculo não é herança — só o role global bypassa.
        var usuario = NovoUsuario("u1", UserRoles.User);
        var grupo = new Group { Id = "g1", Name = "restrito", PermissionsJson = Json(Negando(PermissionService.WorkspaceFiles)) };
        var membro = new GroupMember { GroupId = "g1", UserId = "u1", Role = "admin" };
        await SeedAsync(usuario, grupo, membro);

        Assert.That(await HasAsync(usuario, PermissionService.WorkspaceFiles), Is.False);
    }

    [Test]
    public async Task UsuarioPendente_MembroDeGrupoRestritivo_RetornaFalse()
    {
        // Branch: role "pending" não tem bypass — segue a união dos grupos.
        var usuario = NovoUsuario("u1", UserRoles.Pending);
        var grupo = new Group { Id = "g1", Name = "restrito", PermissionsJson = Json(Negando(PermissionService.ChatControls)) };
        var membro = new GroupMember { GroupId = "g1", UserId = "u1" };
        await SeedAsync(usuario, grupo, membro);

        Assert.That(await HasAsync(usuario, PermissionService.ChatControls), Is.False);
    }

    [Test]
    public async Task Membro_PermissaoDesconhecida_RetornaTrue()
    {
        // Branch: switch default `_` → chaves fora do catálogo retornam true.
        var usuario = NovoUsuario("u1", UserRoles.User);
        var grupo = new Group { Id = "g1", Name = "restrito", PermissionsJson = Json(Negando("qualquer")) };
        var membro = new GroupMember { GroupId = "g1", UserId = "u1" };
        await SeedAsync(usuario, grupo, membro);

        Assert.Multiple(async () =>
        {
            Assert.That(await HasAsync(usuario, "secao.inexistente"), Is.True);
            Assert.That(await HasAsync(usuario, ""), Is.True);
        });
    }

    [Test]
    public async Task Membro_GrupoInexistente_VinculoOrfaoIgnorado()
    {
        // Branch: m.Group! nulo — membro órfão (FK desligada) não contribui na união.
        await using (var seed = CreateContext(enforceForeignKeys: false))
        {
            seed.Users.Add(NovoUsuario("u1", UserRoles.User));
            seed.GroupMembers.Add(new GroupMember { GroupId = "grupo-fantasma", UserId = "u1" });
            await seed.SaveChangesAsync();
        }

        // Se o join interno filtrar o órfão, count == 0 → default true.
        Assert.That(await HasAsync(NovoUsuario("u1", UserRoles.User), PermissionService.WorkspaceModels), Is.True);
    }

    [Test]
    public async Task TokenCancelado_PropagaOperationCanceled()
    {
        // Branch: CancellationToken cancelado propaga pela query EF.
        var usuario = NovoUsuario("u1", UserRoles.User);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.CatchAsync<OperationCanceledException>(
            () => HasAsync(usuario, PermissionService.WorkspaceModels, cts.Token));
    }

    [Test]
    public async Task Override_Usuario_Permite_Onde_Grupo_Nega()
    {
        // SPEC permissions-granular: flag explícita do usuário vence a do grupo.
        var usuario = NovoUsuario("u1", UserRoles.User);
        usuario.PermissionsJson = """{"workspace":{"models":true}}""";
        var grupo = new Group { Id = "g1", Name = "nega-models", PermissionsJson = Json(Negando(PermissionService.WorkspaceModels)) };
        var vinculo = new GroupMember { GroupId = "g1", UserId = "u1" };

        await SeedAsync(usuario, grupo, vinculo);
        Assert.That(await HasAsync(usuario, PermissionService.WorkspaceModels), Is.True);
    }

    [Test]
    public async Task Override_Usuario_Nega_Onde_Grupo_Permite()
    {
        var usuario = NovoUsuario("u1", UserRoles.User);
        usuario.PermissionsJson = """{"workspace":{"tools":false}}""";
        var grupo = new Group { Id = "g1", Name = "completo", PermissionsJson = Json(GroupPermissions.Full) };
        var vinculo = new GroupMember { GroupId = "g1", UserId = "u1" };

        await SeedAsync(usuario, grupo, vinculo);
        Assert.Multiple(async () =>
        {
            Assert.That(await HasAsync(usuario, PermissionService.WorkspaceTools), Is.False);
            Assert.That(await HasAsync(usuario, PermissionService.WorkspaceModels), Is.True);
        });
    }

    [Test]
    public async Task Override_Usuario_NaoAfeta_OutroMembro()
    {
        // Override desliga a flag para um usuário sem afetar outro membro do grupo.
        var u1 = NovoUsuario("u1", UserRoles.User);
        u1.PermissionsJson = """{"workspace":{"models":false}}""";
        var u2 = NovoUsuario("u2", UserRoles.User);
        var grupo = new Group { Id = "g1", Name = "completo", PermissionsJson = Json(GroupPermissions.Full) };
        var m1 = new GroupMember { GroupId = "g1", UserId = "u1" };
        var m2 = new GroupMember { GroupId = "g1", UserId = "u2" };

        await SeedAsync(u1, u2, grupo, m1, m2);
        Assert.Multiple(async () =>
        {
            Assert.That(await HasAsync(u1, PermissionService.WorkspaceModels), Is.False);
            Assert.That(await HasAsync(u2, PermissionService.WorkspaceModels), Is.True);
        });
    }
}

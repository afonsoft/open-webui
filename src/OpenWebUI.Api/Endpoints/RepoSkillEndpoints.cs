using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Repo skills &amp; commands (SPEC-20261009-repo-skills-slash-commands,
/// E16 S3+S4): expõe <c>SKILL.md</c> e commands markdown do workdir do repo
/// vinculado como catálogo para slash commands no composer e para a tool
/// <c>builtin:skill</c>. Sem repo vinculado: 404 <c>{detail, bound:false}</c>.
/// </summary>
public static class RepoSkillEndpoints
{
    /// <summary>Mapeia as rotas de skills/commands dentro do grupo workspace/repo.</summary>
    public static void MapRepoSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var repo = app.MapGroup("/api/v1/workspace/repo").RequireAuthorization();
        repo.MapGet("/skills", ListSkillsAsync);
        repo.MapGet("/skills/{name}", GetSkillAsync);
        repo.MapGet("/commands", ListCommandsAsync);
        repo.MapGet("/commands/{name}", GetCommandAsync);
    }

    private static async Task<IResult> ListSkillsAsync(
        HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        SkillDiscoveryService skills, CancellationToken ct)
    {
        var (_, workdir, reject) = await BoundWorkdirAsync(http, db, repos, ct);
        if (reject is not null)
        {
            return reject;
        }

        var scan = skills.Scan(workdir!);
        return Results.Ok(scan.Skills.Select(s =>
            new RepoSkillItemResponse(s.Name, s.Description, "skill", s.Path)).ToList());
    }

    private static async Task<IResult> GetSkillAsync(
        string name, HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        SkillDiscoveryService skills, CancellationToken ct)
    {
        var (_, workdir, reject) = await BoundWorkdirAsync(http, db, repos, ct);
        if (reject is not null)
        {
            return reject;
        }

        var entry = skills.Scan(workdir!).Skills
            .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return entry is null
            ? Results.NotFound(new { detail = $"Skill '{name}' não encontrada no repositório." })
            : Results.Ok(new RepoSkillDetailResponse(
                entry.Name, entry.Description, "skill", entry.Path, entry.Body));
    }

    private static async Task<IResult> ListCommandsAsync(
        HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        SkillDiscoveryService skills, CancellationToken ct)
    {
        var (_, workdir, reject) = await BoundWorkdirAsync(http, db, repos, ct);
        if (reject is not null)
        {
            return reject;
        }

        var scan = skills.Scan(workdir!);
        return Results.Ok(scan.Commands.Select(c =>
            new RepoCommandItemResponse(
                c.Name, c.Description, c.Agent, c.Model, c.Subtask, "command", c.Path)).ToList());
    }

    private static async Task<IResult> GetCommandAsync(
        string name, HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        SkillDiscoveryService skills, CancellationToken ct)
    {
        var (_, workdir, reject) = await BoundWorkdirAsync(http, db, repos, ct);
        if (reject is not null)
        {
            return reject;
        }

        var entry = skills.Scan(workdir!).Commands
            .FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return entry is null
            ? Results.NotFound(new { detail = $"Command '{name}' não encontrado no repositório." })
            : Results.Ok(new RepoCommandDetailResponse(
                entry.Name, entry.Description, entry.Agent, entry.Model,
                entry.Subtask, "command", entry.Path, entry.Body));
    }

    /// <summary>Guard comum: usuário autenticado + repo vinculado → workdir.</summary>
    private static async Task<(string? UserId, string? Workdir, IResult? Reject)> BoundWorkdirAsync(
        HttpContext http, AppDbContext db, WorkspaceRepoService repos, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return (null, null, Results.Unauthorized());
        }

        var binding = await repos.GetBindingAsync(user.Id, ct);
        if (binding is null)
        {
            return (null, null, Results.NotFound(
                new { detail = "Nenhum repositório vinculado.", bound = false }));
        }

        return (user.Id, await repos.ResolveWorkdirAsync(user.Id, ct), null);
    }
}

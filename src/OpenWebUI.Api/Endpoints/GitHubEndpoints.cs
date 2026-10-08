using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Integração GitHub por usuário + repositório vinculado ao workspace
/// (SPEC-20261008-github-repo-workspace). O PAT fica server-side no kv —
/// a API só devolve status/repos. O repo vinculado vira o workdir das
/// tools <c>file_*</c>/<c>shell_exec</c> e do git snapshot do chat.
/// </summary>
public static class GitHubEndpoints
{
    /// <summary>Mapeia as rotas de GitHub e de workspace-repo.</summary>
    public static void MapGitHubEndpoints(this IEndpointRouteBuilder app)
    {
        var github = app.MapGroup("/api/v1/github").RequireAuthorization();
        github.MapGet("/config", GetConfigAsync);
        github.MapPut("/config", SetTokenAsync);
        github.MapDelete("/config", ClearTokenAsync);
        github.MapGet("/repos", ListReposAsync);
        github.MapGet("/repos/{owner}/{repo}/branches", ListBranchesAsync);

        var wrepo = app.MapGroup("/api/v1/workspace/repo").RequireAuthorization();
        wrepo.MapGet("/", GetBindingAsync);
        wrepo.MapPost("/open", OpenRepoAsync);
        wrepo.MapDelete("/", UnbindRepoAsync);
    }

    private static async Task<IResult> GetConfigAsync(
        HttpContext http, AppDbContext db, GitHubService github, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await github.GetStatusAsync(user.Id, ct));
    }

    /// <summary>Valida e persiste o PAT; o token nunca volta em resposta.</summary>
    private static async Task<IResult> SetTokenAsync(
        GitHubTokenRequest request,
        HttpContext http, AppDbContext db, GitHubService github, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return Results.BadRequest(new { detail = "Token é obrigatório." });
        }

        var login = await github.SetTokenAsync(user.Id, request.Token.Trim(), ct);
        if (login is null)
        {
            return Results.BadRequest(new { detail = "Token inválido ou GitHub indisponível." });
        }

        return Results.Ok(new GitHubConfigResponse(true, login));
    }

    private static async Task<IResult> ClearTokenAsync(
        HttpContext http, AppDbContext db, GitHubService github, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        await github.ClearTokenAsync(user.Id, ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> ListReposAsync(
        HttpContext http, AppDbContext db, GitHubService github, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await github.ListReposAsync(user.Id, ct));
    }

    private static async Task<IResult> ListBranchesAsync(
        string owner, string repo,
        HttpContext http, AppDbContext db, GitHubService github, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var branches = await github.ListBranchesAsync(user.Id, owner, repo, ct);
        return branches is null
            ? Results.NotFound(new { detail = "Repositório não encontrado ou sem acesso." })
            : Results.Ok(branches);
    }

    private static async Task<IResult> GetBindingAsync(
        HttpContext http, AppDbContext db, WorkspaceRepoService repos, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var binding = await repos.GetBindingAsync(user.Id, ct);
        return Results.Ok(new WorkspaceRepoResponse(
            binding?.Repo, binding?.Branch, binding?.Dir));
    }

    /// <summary>Clona (ou troca de branch) o repo no workspace do usuário.</summary>
    private static async Task<IResult> OpenRepoAsync(
        WorkspaceRepoOpenRequest request,
        HttpContext http, AppDbContext db,
        WorkspaceRepoService repos, GitHubService github, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (string.IsNullOrWhiteSpace(request.Repo) || string.IsNullOrWhiteSpace(request.Branch))
        {
            return Results.BadRequest(new { detail = "repo e branch são obrigatórios." });
        }

        var slug = request.Repo.Trim();
        var token = await github.GetTokenAsync(user.Id, ct);
        var (binding, error) = await repos.OpenAsync(
            user.Id, slug, request.Branch.Trim(),
            $"https://github.com/{slug}.git", token, ct);
        return binding is null
            ? Results.BadRequest(new { detail = error })
            : Results.Ok(new WorkspaceRepoResponse(binding.Repo, binding.Branch, binding.Dir));
    }

    private static async Task<IResult> UnbindRepoAsync(
        HttpContext http, AppDbContext db, WorkspaceRepoService repos, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        await repos.UnbindAsync(user.Id, ct);
        return Results.Ok(new StatusResponse(true));
    }
}

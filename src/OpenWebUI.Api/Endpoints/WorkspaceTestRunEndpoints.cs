using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Rotas do runner de testes do workspace-repo
/// (SPEC-20261009-ide-mentions-tests, RF-003/RF-004):
/// POST /api/v1/workspace/repo/test-run detecta o comando pelo manifesto
/// (ou TestCommand do binding) e executa como ChatJob dentro do jail;
/// GET /api/v1/workspace/repo/test-run/{jobId} devolve estado + resumo
/// parseado + cauda do log. Comandos WorkspaceWrite exigem
/// <c>confirmed</c> — o comando é devolvido visível antes da aprovação.
/// </summary>
public static class WorkspaceTestRunEndpoints
{
    /// <summary>Cap da cauda do log retornada ao cliente (chars).</summary>
    private const int TailChars = 8_000;

    /// <summary>Mapeia as rotas de test-run no mesmo prefixo do workspace/repo.</summary>
    public static void MapWorkspaceTestRunEndpoints(this IEndpointRouteBuilder app)
    {
        var repo = app.MapGroup("/api/v1/workspace/repo").RequireAuthorization();
        repo.MapPost("/test-run", StartAsync);
        repo.MapGet("/test-run/{jobId}", GetAsync);
        repo.MapPut("/test-command", SetTestCommandAsync);
    }

    /// <summary>Inicia o test run; 404 sem binding, 422 sem manifesto ou comando negado.</summary>
    private static async Task<IResult> StartAsync(
        TestRunStartRequest request,
        HttpContext http, AppDbContext db,
        WorkspaceRepoService repos, ChatJobService jobs, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var binding = await repos.GetBindingAsync(user.Id, ct);
        if (binding is null)
        {
            return Results.NotFound(new { detail = "Nenhum repositório vinculado.", bound = false });
        }
        var workdir = await repos.ResolveWorkdirAsync(user.Id, ct);

        var command = TestCommandDetector.Detect(workdir, binding.TestCommand);
        if (command is null)
        {
            return Results.Json(new
            {
                detail = "Nenhum manifesto de testes conhecido no workdir.",
                suggested = "Defina TestCommand no binding do repositório (PUT /api/v1/workspace/repo/test-command).",
            }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var assessment = CommandRiskClassifier.Classify(command, workdir);
        if (!assessment.Allowed)
        {
            return Results.Json(new
            {
                detail = $"Comando de teste negado pela política de risco: {assessment.Reason}",
                suggested = default(string),
            }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        // WorkspaceWrite → cartão de aprovação: o comando volta visível e só roda com confirmed=true.
        if (assessment.Level == CommandRiskLevel.WorkspaceWrite && !request.Confirmed)
        {
            return Results.Ok(new TestRunStartResponse(null, command, true, assessment.Reason));
        }

        var job = await jobs.StartAsync(
            command,
            new BuiltinToolContext(user.Id, null, null, workdir, workdir),
            ct);
        return Results.Ok(new TestRunStartResponse(job.Id, command, false, null));
    }

    /// <summary>Estado do job + resumo parseado + cauda do log; 404 quando o job não existe.</summary>
    private static async Task<IResult> GetAsync(
        string jobId,
        HttpContext http, AppDbContext db, ChatJobService jobs, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var result = await jobs.GetOutputAsync(user.Id, jobId, TailChars, ct);
        if (result is null)
        {
            return Results.NotFound(new { detail = "Test run não encontrado." });
        }

        var (job, output) = result.Value;
        TestRunSummary? summary = null;
        if (job.Status != ChatJobStatus.Running)
        {
            var counts = TestRunOutputParser.Parse(job.Command, output);
            var durationMs = job.FinishedAt is { } end
                ? (end - job.StartedAt) * 1000
                : (long?)null;
            summary = new TestRunSummary(counts?.Passed, counts?.Failed, counts?.Skipped, durationMs);
        }
        return Results.Ok(new TestRunStatusResponse(
            job.Status, summary, output, job.Command, job.ExitCode, job.Error));
    }

    /// <summary>Define ou limpa o TestCommand customizado do binding (override do manifesto).</summary>
    private static async Task<IResult> SetTestCommandAsync(
        TestCommandRequest request,
        HttpContext http, AppDbContext db,
        WorkspaceRepoService repos, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var binding = await repos.GetBindingAsync(user.Id, ct);
        if (binding is null)
        {
            return Results.NotFound(new { detail = "Nenhum repositório vinculado.", bound = false });
        }

        await repos.SetTestCommandAsync(user.Id, request.TestCommand, ct);
        return Results.Ok(new StatusResponse(true));
    }
}

using Microsoft.AspNetCore.Mvc;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Endpoints REST dos jobs de background do chat
/// (SPEC-20261007-chat-agent-tools RF-004): espelham as tools
/// <c>job_list/job_output/job_kill</c> para a UI — listar (do chat ou todos
/// do usuário), ler saída (tail) e matar. Isolamento sempre por usuário.
/// </summary>
public static class ChatJobEndpoints
{
    /// <summary>Mapeia o grupo /api/v1/jobs.</summary>
    public static void MapChatJobEndpoints(this WebApplication app)
    {
        var jobs = app.MapGroup("/api/v1/jobs").RequireAuthorization();
        jobs.MapGet("/", ListAsync);
        jobs.MapGet("/{jobId}", GetAsync);
        jobs.MapGet("/{jobId}/output", OutputAsync);
        jobs.MapPost("/{jobId}/kill", KillAsync);
    }

    /// <summary>Lista os jobs do usuário (filtra por <c>chatId</c> quando dado).</summary>
    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db, ChatJobService jobs,
        [FromQuery] string? chatId, [FromQuery] bool? all, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var list = await jobs.ListAsync(user.Id, chatId, includeFinished: all == true, ct);
        return Results.Ok(list.Select(ToResponse));
    }

    /// <summary>Devolve o job (metadados) se pertencer ao usuário.</summary>
    private static async Task<IResult> GetAsync(
        string jobId, HttpContext http, AppDbContext db, ChatJobService jobs,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var job = await jobs.GetAsync(user.Id, jobId, ct);
        return job is null ? Results.NotFound() : Results.Ok(ToResponse(job));
    }

    /// <summary>Devolve a saída capturada do job (últimos <c>tail</c> chars).</summary>
    private static async Task<IResult> OutputAsync(
        string jobId, HttpContext http, AppDbContext db, ChatJobService jobs,
        [FromQuery] int? tail, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var result = await jobs.GetOutputAsync(
            user.Id, jobId, Math.Clamp(tail is null or <= 0 ? 8_000 : tail.Value, 100, 64_000), ct);
        return result is null
            ? Results.NotFound()
            : Results.Ok(new
            {
                job = ToResponse(result.Value.Job),
                output = result.Value.Output,
            });
    }

    /// <summary>Mata o processo do job (árvore inteira) se ainda estiver vivo.</summary>
    private static async Task<IResult> KillAsync(
        string jobId, HttpContext http, AppDbContext db, ChatJobService jobs,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var killed = await jobs.KillAsync(user.Id, jobId, ct);
        return killed
            ? Results.Ok(new { killed = true })
            : Results.NotFound(new { detail = "Job não encontrado ou já finalizado." });
    }

    /// <summary>Projeção do job para o cliente (sem caminhos internos do host).</summary>
    private static ChatJobResponse ToResponse(ChatJob j) => new(
        j.Id, j.ChatId, j.RunId, j.Command, j.Status, j.Pid, j.ExitCode,
        j.Error, j.StartedAt, j.FinishedAt);
}

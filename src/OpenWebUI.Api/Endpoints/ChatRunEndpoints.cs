using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Runs;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Rotas de runs de chat desacopladas (SPEC-20261007-chat-detached-runs),
/// mapeadas sob o grupo <c>/api/v1/chats</c>:
/// envio de mensagem → run em background; attach SSE com replay por
/// <c>lastSeq</c>; stop; listagem. A geração sobrevive a reload/fechamento
/// da aba — o cliente anexa ao stream quando volta.
/// </summary>
public static class ChatRunEndpoints
{
    /// <summary>Mapeia as rotas de runs dentro do grupo de chats.</summary>
    public static void MapChatRunEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/{id}/messages", EnqueueMessageAsync);
        group.MapGet("/{id}/runs", ListRunsAsync);
        group.MapGet("/{id}/runs/active", GetActiveRunAsync);
        group.MapGet("/{id}/runs/{runId}", GetRunAsync);
        group.MapGet("/{id}/runs/{runId}/stream", StreamRunAsync);
        group.MapPost("/{id}/runs/{runId}/stop", StopRunAsync);
        group.MapPost("/{id}/runs/{runId}/approvals/{callId}", DecideApprovalAsync);
    }

    /// <summary>
    /// Persiste a mensagem do usuário, cria a run queued e enfileira no
    /// dispatcher. Responde 202 + snapshot da run (o cliente abre o attach).
    /// </summary>
    private static async Task<IResult> EnqueueMessageAsync(
        string id,
        EnqueueChatRunRequest request,
        HttpContext http,
        AppDbContext db,
        ChatRunDispatcher dispatcher,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            return Results.BadRequest(new { detail = "model é obrigatório." });
        }

        var chat = await db.Chats.Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == user.Id, ct);
        if (chat is null)
        {
            return Results.NotFound(new { detail = "Chat não encontrado." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (!string.IsNullOrWhiteSpace(request.Content))
        {
            // Persiste a mensagem do usuário já no servidor (o cliente não precisa reenviar o chat inteiro).
            var nextPosition = chat.Messages.Count == 0
                ? 0
                : chat.Messages.Max(m => m.Position) + 1;
            chat.Messages.Add(new ChatMessage
            {
                ChatId = chat.Id,
                Role = "user",
                Content = request.Content,
                Position = nextPosition,
                Timestamp = now,
            });
            chat.UpdatedAt = now;
        }
        else if (chat.Messages.OrderByDescending(m => m.Position).FirstOrDefault()?.Role != "user")
        {
            // Sem conteúdo novo, a run regenera sobre o histórico — que precisa terminar em msg do usuário.
            return Results.BadRequest(new { detail = "Histórico não termina em mensagem do usuário." });
        }

        // Histórico reconstruído no servidor — a fonte da verdade é o banco.
        var history = chat.Messages
            .OrderBy(m => m.Position)
            .Select(m => new ChatCompletionMessage(
                m.Role, m.Content, m.ToolCallId, m.ToolCallsJson))
            .ToList();
        var completionRequest = new ChatCompletionRequest(
            request.Model, history, Stream: true,
            FileIds: request.FileIds, Params: request.Params,
            ToolIds: request.ToolIds, WebSearch: request.WebSearch);

        var run = new ChatRun
        {
            ChatId = chat.Id,
            UserId = user.Id,
            Model = request.Model,
            RequestJson = JsonSerializer.Serialize(completionRequest, JsonOptions),
            CreatedAt = now,
        };
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync(ct);

        dispatcher.Enqueue(run.Id);
        return Results.Accepted(value: ToResponse(run));
    }

    /// <summary>Lista as runs mais recentes do chat (máx. 20).</summary>
    private static async Task<IResult> ListRunsAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var runs = await db.ChatRuns.AsNoTracking()
            .Where(r => r.ChatId == id && r.UserId == user.Id)
            .OrderByDescending(r => r.CreatedAt)
            .Take(20)
            .ToListAsync(ct);
        return Results.Ok(runs.Select(ToResponse));
    }

    /// <summary>Retorna a run ativa (queued/running) do chat, ou null.</summary>
    private static async Task<IResult> GetActiveRunAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var run = await db.ChatRuns.AsNoTracking()
            .Where(r => r.ChatId == id && r.UserId == user.Id
                && (r.Status == ChatRunStatus.Queued || r.Status == ChatRunStatus.Running))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return run is null ? Results.Ok() : Results.Ok(ToResponse(run));
    }

    /// <summary>Snapshot de uma run (status + conteúdo parcial).</summary>
    private static async Task<IResult> GetRunAsync(
        string id, string runId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var run = await db.ChatRuns.AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Id == runId && r.ChatId == id && r.UserId == user.Id, ct);
        return run is null
            ? Results.NotFound(new { detail = "Run não encontrada." })
            : Results.Ok(ToResponse(run));
    }

    /// <summary>
    /// Attach SSE: replay do backlog com seq &gt; <c>lastSeq</c> e depois o
    /// stream vivo até a run fechar. Cada evento sai como
    /// <c>id: {seq}\n{payload}\n\n</c> — o cliente resume por Last-Event-ID.
    /// </summary>
    private static async Task StreamRunAsync(
        string id,
        string runId,
        HttpContext http,
        AppDbContext db,
        ChatRunBroadcaster broadcaster,
        int? lastSeq,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            http.Response.StatusCode = 401;
            return;
        }

        var run = await db.ChatRuns.AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Id == runId && r.ChatId == id && r.UserId == user.Id, ct);
        if (run is null)
        {
            http.Response.StatusCode = 404;
            return;
        }

        // Last-Event-ID do SSE também conta como checkpoint de replay.
        var since = lastSeq ?? 0;
        if (since == 0
            && http.Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId)
            && int.TryParse(lastEventId, out var parsed))
        {
            since = parsed;
        }

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Connection = "keep-alive";

        await using var writer = new StreamWriter(http.Response.Body, Encoding.UTF8);
        try
        {
            await foreach (var evt in broadcaster.SubscribeAsync(runId, since, ct))
            {
                await writer.WriteLineAsync($"id: {evt.Seq}");
                await writer.WriteLineAsync(evt.Payload);
                await writer.WriteLineAsync();
                await writer.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Cliente desconectou — normal: a run continua no dispatcher.
        }
    }

    /// <summary>Pede a interrupção de uma run ativa.</summary>
    private static async Task<IResult> StopRunAsync(
        string id,
        string runId,
        HttpContext http,
        AppDbContext db,
        ChatRunDispatcher dispatcher,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var run = await db.ChatRuns
            .FirstOrDefaultAsync(
                r => r.Id == runId && r.ChatId == id && r.UserId == user.Id, ct);
        if (run is null)
        {
            return Results.NotFound(new { detail = "Run não encontrada." });
        }

        if (run.Status == ChatRunStatus.Queued)
        {
            // Ainda não iniciou: cancela direto (o dispatcher pula status != queued).
            run.Status = ChatRunStatus.Stopped;
            run.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(run));
        }

        if (run.Status == ChatRunStatus.Running)
        {
            dispatcher.TryStop(run.Id);
            return Results.Ok(ToResponse(run));
        }

        return Results.Conflict(new { detail = $"Run já finalizada ({run.Status})." });
    }

    /// <summary>
    /// Decisão do dono sobre uma aprovação de tool pendente
    /// (SPEC-20261007-chat-tool-streaming RF-003): <c>approve</c> | <c>deny</c>
    /// + <c>remember</c> opcional (válido só nesta conversa, em memória).
    /// 404 quando a call não está pendente; 410 quando a run já finalizou.
    /// </summary>
    private static async Task<IResult> DecideApprovalAsync(
        string id,
        string runId,
        string callId,
        RunApprovalDecisionRequest request,
        HttpContext http,
        AppDbContext db,
        ChatRunApprovals approvals,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (request.Decision is not ("approve" or "deny"))
        {
            return Results.BadRequest(new { detail = "decision deve ser approve|deny." });
        }

        var run = await db.ChatRuns.AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Id == runId && r.ChatId == id && r.UserId == user.Id, ct);
        if (run is null)
        {
            return Results.NotFound(new { detail = "Run não encontrada." });
        }
        if (run.Status is not (ChatRunStatus.Queued or ChatRunStatus.Running))
        {
            return Results.Json(
                new { detail = $"Run já finalizada ({run.Status})." },
                statusCode: StatusCodes.Status410Gone);
        }

        if (request.Message is { Length: > 2048 })
        {
            return Results.BadRequest(new { detail = "message deve ter até 2KB." });
        }

        var approved = request.Decision == "approve";
        return approvals.Resolve(
            run.Id, run.ChatId, callId, approved, request.Remember,
            approved ? null : request.Message)
            ? Results.Ok(new StatusResponse(true))
            : Results.NotFound(new { detail = "Aprovação não está pendente." });
    }

    private static ChatRunResponse ToResponse(ChatRun run) => new(
        run.Id, run.ChatId, run.Status, run.Model,
        run.PartialContent, run.Error,
        run.CreatedAt, run.StartedAt, run.CompletedAt);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

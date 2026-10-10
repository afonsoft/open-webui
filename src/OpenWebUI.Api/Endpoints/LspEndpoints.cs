using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Lsp;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Endpoints LSP do editor IDE (SPEC-20261009-lsp-diagnostics): status por
/// arquivo (degrade limpo quando a linguagem não tem servidor), sync de
/// documento (didOpen/didChange/didClose vindos do editor) e consultas
/// (diagnostics, hover) usadas pelo painel Problems e pelo tooltip.
/// Tudo atrás do guard de repo vinculado (<c>BoundWorkdirAsync</c>).
/// </summary>
public static class LspEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia as rotas LSP do workspace.</summary>
    public static RouteGroupBuilder MapLspEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/workspace/lsp").RequireAuthorization();
        group.MapGet("/status", StatusAsync);
        group.MapPost("/doc", DocSyncAsync);
        group.MapGet("/diagnostics", DiagnosticsAsync);
        group.MapGet("/hover", HoverAsync);
        return group;
    }

    // ---------- GET /status?path= ----------

    /// <summary>
    /// Linguagem/estado do servidor do arquivo — UI usa para decidir se
    /// renderiza Problems/squiggles (state "unavailable"/null → nada).
    /// </summary>
    private static Task<IResult> StatusAsync(
        string? path, string? chatId, HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        LspService lsp, CancellationToken ct) =>
        WithWorkdirAsync(http, db, repos, chatId, ct, (uid, workdir) =>
        {
            var language = lsp.LanguageFor(path);
            if (language is null)
            {
                return Task.FromResult(Results.Ok(
                    new LspStatusResponse(lsp.Enabled, null, "unmapped", null)));
            }
            return Task.FromResult(Results.Ok(new
            {
                enabled = lsp.Enabled,
                language = language.ServerKey,
                state = lsp.StateOf(workdir, language.ServerKey).ToString().ToLowerInvariant(),
                error = lsp.ErrorOf(workdir, language.ServerKey),
            }));
        });

    // ---------- POST /doc ----------

    /// <summary>
    /// Sync do documento a partir do editor: <c>kind</c> open|change|close.
    /// didOpen/didChange carregam o texto (v1 full-sync); close é best-effort.
    /// Responde 200 mesmo sem servidor (degrade) — o campo
    /// <c>synced:false</c> informa que nada foi enviado.
    /// </summary>
    private static Task<IResult> DocSyncAsync(
        [FromQuery] string? chatId, HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        LspService lsp, CancellationToken ct) =>
        WithWorkdirAsync(http, db, repos, chatId, ct, async (uid, workdir) =>
        {
            var request = await JsonSerializer.DeserializeAsync<LspDocSyncRequest>(
                http.Request.Body, JsonOptions, ct);
            if (request is null || string.IsNullOrWhiteSpace(request.Path))
            {
                return Results.BadRequest(new { detail = "'path' é obrigatório." });
            }

            var full = WorkspaceFiles.ResolveInside(workdir, request.Path, out var error);
            if (full is null)
            {
                return Results.BadRequest(new { detail = error });
            }
            if (request.Kind is not "close" && !File.Exists(full) && request.Text is null)
            {
                return Results.NotFound(new { detail = "Arquivo não existe." });
            }

            var synced = false;
            try
            {
                switch (request.Kind)
                {
                    case "open":
                        synced = await lsp.OpenDocumentAsync(
                            workdir, full, request.Text, ct) is not null;
                        break;
                    case "change":
                        if (request.Text is null)
                        {
                            return Results.BadRequest(new { detail = "'text' é obrigatório." });
                        }
                        await lsp.ChangeDocumentAsync(workdir, full, request.Text, ct);
                        synced = lsp.LanguageFor(full) is not null
                            && lsp.StateOf(workdir, lsp.LanguageFor(full)!.ServerKey)
                                == LspServerState.Running;
                        break;
                    case "close":
                        await lsp.CloseDocumentAsync(workdir, full, ct);
                        synced = true;
                        break;
                    default:
                        return Results.BadRequest(
                            new { detail = "'kind' deve ser open|change|close." });
                }
            }
            catch (LspUnavailableException ex)
            {
                return Results.Ok(new { synced = false, detail = ex.Message });
            }
            return Results.Ok(new { synced });
        });

    // ---------- GET /diagnostics?path= ----------

    /// <summary>
    /// Diagnostics do arquivo (ou do workdir sem <c>path</c>) no formato
    /// compacto do editor: <c>path, line, col, endLine, endCol, severity,
    /// code, source, message</c> — cap <see cref="LspOptions.ResultCap"/>.
    /// </summary>
    private static Task<IResult> DiagnosticsAsync(
        string? path, string? chatId, HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        LspService lsp, CancellationToken ct) =>
        WithWorkdirAsync(http, db, repos, chatId, ct, async (uid, workdir) =>
        {
            string? full = null;
            if (path is not null)
            {
                full = WorkspaceFiles.ResolveInside(workdir, path, out var error);
                if (full is null)
                {
                    return Results.BadRequest(new { detail = error });
                }
            }

            // Se o doc ainda não foi publicado, espera o primeiro push (cap 8s)
            // — cobre o fluxo open→diagnostics do editor sem poling.
            var language = lsp.LanguageFor(full);
            if (full is not null && language is not null
                && lsp.StateOf(workdir, language.ServerKey) == LspServerState.Running)
            {
                try
                {
                    var client = await lsp.GetClientAsync(workdir, language, ct);
                    await client.AwaitDiagnosticsAsync(full, TimeSpan.FromSeconds(8), ct);
                }
                catch (LspUnavailableException)
                {
                    // servidor caiu entre status e diagnostics — responde vazio.
                }
            }

            var diags = lsp.Diagnostics(workdir, full, out _);
            var capped = diags.Take(LspOptions.ResultCap).Select(d => new LspDiagnosticItem(
                LspResponse.RelPath(workdir, d.Path),
                d.Line, d.Col, d.EndLine, d.EndCol,
                d.Severity, d.Code, d.Source, d.Message)).ToList();
            return Results.Ok(new LspDiagnosticsResponse(
                capped, diags.Count > LspOptions.ResultCap, diags.Count));
        });

    // ---------- GET /hover?path=&line=&col= ----------

    /// <summary>Tooltip do editor — hover do servidor (texto ou null).</summary>
    private static Task<IResult> HoverAsync(
        string? path, int? line, int? col, string? chatId,
        HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        LspService lsp, CancellationToken ct) =>
        WithWorkdirAsync(http, db, repos, chatId, ct, async (uid, workdir) =>
        {
            var full = WorkspaceFiles.ResolveInside(workdir, path, out var error);
            if (full is null)
            {
                return Results.BadRequest(new { detail = error });
            }

            var language = lsp.LanguageFor(full);
            if (language is null)
            {
                return Results.Ok(new LspHoverResponse(null));
            }
            try
            {
                var client = await lsp.GetClientAsync(workdir, language, ct);
                var result = await client.RequestAsync("textDocument/hover", new
                {
                    textDocument = new { uri = LspClient.UriForPath(full) },
                    position = new
                    {
                        line = Math.Max(0, (line ?? 1) - 1),
                        character = Math.Max(0, (col ?? 1) - 1),
                    },
                }, ct);
                return Results.Ok(new LspHoverResponse(LspResponse.HoverText(result)));
            }
            catch (Exception ex) when (ex is LspUnavailableException or LspRequestException
                or IOException or TimeoutException)
            {
                return Results.Ok(new LspHoverResponse(null));
            }
        });

    // ---------- guard comum ----------

    /// <summary>Repo vinculado → executa o handler com (userId, workdir).</summary>
    private static async Task<IResult> WithWorkdirAsync(
        HttpContext http, AppDbContext db, WorkspaceRepoService repos, string? chatId,
        CancellationToken ct, Func<string, string, Task<IResult>> handler)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (chatId is not null
            && !await db.Chats.AsNoTracking().AnyAsync(c => c.Id == chatId && c.UserId == user.Id, ct))
        {
            return Results.NotFound(new { detail = "Chat não encontrado." });
        }
        if ((await repos.ResolveBindingAsync(user.Id, chatId, ct)).Binding is null)
        {
            return Results.NotFound(
                new { detail = "Nenhum repositório vinculado.", bound = false });
        }
        return await handler(user.Id, await repos.ResolveWorkdirAsync(user.Id, chatId, ct));
    }

}

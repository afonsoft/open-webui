using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Workspace File API (SPEC-20261009-workspace-file-api, E16 S1): REST sobre
/// o workdir do repositório vinculado — tree paginada, leitura fatiada com
/// ETag, escrita com If-Match e operações de estrutura. Todo caminho passa
/// por <see cref="WorkspaceFiles.ResolveInsideFinal"/> (jail + symlink).
/// Sem repo vinculado: 404 <c>{detail, bound:false}</c> em todas as rotas.
/// </summary>
public static class WorkspaceFileEndpoints
{
    /// <summary>Diretórios gerados/volumosos — listados colapsados, sem filhos.</summary>
    private static readonly HashSet<string> CollapsedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules",
    };

    private const int DefaultMaxLines = 2000;
    private const int MaxLinesCap = 10_000;

    /// <summary>Mapeia as rotas da file API dentro do grupo workspace/repo.</summary>
    public static void MapWorkspaceFileEndpoints(this IEndpointRouteBuilder app)
    {
        var repo = app.MapGroup("/api/v1/workspace/repo").RequireAuthorization();
        repo.MapGet("/tree", TreeAsync);
        repo.MapGet("/file", ReadAsync);
        repo.MapPut("/file", WriteAsync);
        repo.MapPost("/mkdir", MkdirAsync);
        repo.MapPost("/rename", RenameAsync);
        repo.MapPost("/delete", DeleteAsync);
        repo.MapGet("/git", GitAsync);
    }

    /// <summary>Guard comum: usuário autenticado + repo vinculado → workdir.</summary>
    private static async Task<(string? UserId, string? Workdir, IResult? Reject)> BoundWorkdirAsync(
        HttpContext http, AppDbContext db, WorkspaceRepoService repos, string? chatId, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return (null, null, Results.Unauthorized());
        }

        // chatId opcional (SPEC-20261010-workspace-chatid-scope): só vale se o
        // chat é do usuário; senão 404 como toda rota de chat.
        if (chatId is not null
            && !await db.Chats.AsNoTracking().AnyAsync(c => c.Id == chatId && c.UserId == user.Id, ct))
        {
            return (null, null, Results.NotFound(new { detail = "Chat não encontrado." }));
        }

        var binding = await repos.ResolveBindingAsync(user.Id, chatId, ct);
        if (binding.Binding is null)
        {
            return (null, null, Unbound());
        }

        return (user.Id, await repos.ResolveWorkdirAsync(user.Id, chatId, ct), null);
    }

    private static IResult Unbound() =>
        Results.NotFound(new { detail = "Nenhum repositório vinculado.", bound = false });

    private static IResult NotFoundResult() =>
        Results.NotFound(new { ok = false, error = "Caminho não encontrado." });

    // ---------- GET /git ----------

    /// <summary>Snapshot git do workdir (S2: aba Changes do /ide — mesma
    /// carga de <c>GET /chats/{id}/runs/{runId}/diff</c>, sem run).</summary>
    private static async Task<IResult> GitAsync(
        string? chatId, HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        WorkspaceGitService git, CancellationToken ct)
    {
        var (_, w, reject) = await BoundWorkdirAsync(http, db, repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }
        var workdir = w!;

        var info = await git.GetInfoAsync(workdir, ct);
        return Results.Ok(new WorkspaceGitResponse(
            info.IsRepo, info.Branch, info.Added, info.Removed,
            info.Files.Select(f => new WorkspaceGitFileResponse(
                f.Path, f.Added, f.Removed, f.Status)).ToList(),
            info.Diff, info.DiffTruncated));
    }

    // ---------- GET /tree ----------

    private static async Task<IResult> TreeAsync(
        string? path, int? depth, string? cursor, string? chatId,
        HttpContext http, AppDbContext db, WorkspaceRepoService repos, CancellationToken ct)
    {
        var (uid, w, reject) = await BoundWorkdirAsync(http, db, repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }
        var workdir = w!;

        var levels = Math.Clamp(depth ?? 1, 1, 3);

        var full = WorkspaceFiles.ResolveInsideFinal(workdir, path, out var error);
        if (full is null)
        {
            return Results.BadRequest(new { detail = error });
        }
        if (!Directory.Exists(full))
        {
            return NotFoundResult();
        }

        var skip = int.TryParse(cursor, out var c) && c > 0 ? c : 0;
        var entries = Walk(workdir, full, levels).Skip(skip)
            .Take(WorkspaceFiles.MaxEntries + 1).ToList();
        var truncated = entries.Count > WorkspaceFiles.MaxEntries;
        if (truncated)
        {
            entries.RemoveAt(entries.Count - 1);
        }

        var rel = WorkspaceFiles.RelativeOf(workdir, full);
        return Results.Ok(new WorkspaceFileTreeResponse(
            rel, entries,
            truncated ? (skip + WorkspaceFiles.MaxEntries).ToString() : null,
            truncated));
    }

    /// <summary>DFS lazy: dirs primeiro (nome ordinal), depois arquivos.
    /// <paramref name="level"/> = profundidade das entradas emitidas (raiz = 1).</summary>
    private static IEnumerable<WorkspaceFileEntryResponse> Walk(
        string workdir, string dir, int maxDepth, int level = 1)
    {
        IEnumerable<string> dirs;
        IEnumerable<string> files;
        try
        {
            dirs = Directory.EnumerateDirectories(dir)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
            files = Directory.EnumerateFiles(dir)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            // Symlink de dir: nunca recursar — ResolveInsideFinal ainda barra acesso
            // direto, mas listar filhos vazaria nomes fora do jail. Emite colapsado.
            var collapsed = CollapsedDirs.Contains(name) || new DirectoryInfo(d).LinkTarget is not null;
            yield return new WorkspaceFileEntryResponse(
                name, WorkspaceFiles.RelativeOf(workdir, d), "dir", null, collapsed);
            if (!collapsed && level < maxDepth)
            {
                foreach (var child in Walk(workdir, d, maxDepth, level + 1))
                {
                    yield return child;
                }
            }
        }

        foreach (var f in files)
        {
            long size;
            try
            {
                size = new FileInfo(f).Length;
            }
            catch (IOException)
            {
                continue; // removido entre enumerate e stat (TOCTOU)
            }
            yield return new WorkspaceFileEntryResponse(
                Path.GetFileName(f), WorkspaceFiles.RelativeOf(workdir, f),
                "file", size, false);
        }
    }

    // ---------- GET /file ----------

    private static async Task<IResult> ReadAsync(
        string? path, int? startLine, int? maxLines, string? chatId,
        HttpContext http, AppDbContext db, WorkspaceRepoService repos, HttpResponse response,
        CancellationToken ct)
    {
        var (uid, w, reject) = await BoundWorkdirAsync(http, db, repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }
        var workdir = w!;

        var full = WorkspaceFiles.ResolveInsideFinal(workdir, path, out var error);
        if (full is null)
        {
            return Results.BadRequest(new { detail = error });
        }
        if (!File.Exists(full) || Directory.Exists(full))
        {
            return NotFoundResult();
        }

        var size = new FileInfo(full).Length;
        if (WorkspaceFiles.LooksBinary(full))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }
        if (size > WorkspaceFiles.MaxFileBytes)
        {
            return Results.Json(new { detail = "Arquivo acima do limite de leitura.", size },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var lines = await File.ReadAllLinesAsync(full, ct);
        // startLine é 1-based (linhas de editor); 0/ausente = início.
        var start = Math.Clamp((startLine ?? 1) - 1, 0, lines.Length);
        var take = maxLines is null or <= 0 ? DefaultMaxLines : Math.Min(maxLines.Value, MaxLinesCap);
        var slice = lines.Skip(start).Take(take);
        var truncated = start + take < lines.Length;

        var etag = ETagOf(full, size);
        var lastModified = File.GetLastWriteTimeUtc(full);
        response.Headers.ETag = etag;
        response.Headers.LastModified = lastModified.ToString("R");

        return Results.Ok(new WorkspaceFileReadResponse(
            WorkspaceFiles.RelativeOf(workdir, full),
            string.Join("\n", slice),
            lines.Length, truncated, etag));
    }

    // ---------- PUT /file ----------

    private static async Task<IResult> WriteAsync(
        WorkspaceFileWriteRequest request, [FromQuery] string? chatId,
        HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        OpenWebUI.Infrastructure.Lsp.LspService lsp,
        ILogger<Program> logger, CancellationToken ct)
    {
        var (uid, w, reject) = await BoundWorkdirAsync(http, db, repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }
        var workdir = w!;

        var full = WorkspaceFiles.ResolveInsideFinal(workdir, request.Path, out var error);
        if (full is null)
        {
            return Results.BadRequest(new { detail = error });
        }

        var content = request.Content ?? string.Empty;
        if (content.Contains('\0'))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        if (Directory.Exists(full))
        {
            return Results.Conflict(new { ok = false, error = "O caminho é um diretório." });
        }

        var ifMatch = http.Request.Headers.IfMatch.ToString();
        if (File.Exists(full) && !string.IsNullOrEmpty(ifMatch) && ifMatch.Trim() != "*"
            && !ifMatch.Split(',').Select(t => t.Trim()).Contains(ETagOf(full, new FileInfo(full).Length)))
        {
            return Results.Json(
                new { detail = "Conteúdo divergente — recarregue antes de sobrescrever.",
                    etag = ETagOf(full, new FileInfo(full).Length) },
                statusCode: StatusCodes.Status409Conflict);
        }

        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        await File.WriteAllTextAsync(full, content, ct);
        // LSP didSave — best-effort, nunca falha o PUT (SPEC S8).
        await lsp.NotifyFileSavedAsync(workdir, full, ct);

        var etag = ETagOf(full, new FileInfo(full).Length);
        var lastModified = File.GetLastWriteTimeUtc(full);
        logger.LogInformation(
            "workspace-file write user={UserId} path={Path} bytes={Bytes}",
            uid, WorkspaceFiles.RelativeOf(workdir, full), content.Length);
        return Results.Ok(new WorkspaceFileWriteResponse(
            true, WorkspaceFiles.RelativeOf(workdir, full), etag, lastModified));
    }

    // ---------- POST /mkdir ----------

    private static async Task<IResult> MkdirAsync(
        WorkspaceFileMkdirRequest request, [FromQuery] string? chatId,
        HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        ILogger<Program> logger, CancellationToken ct)
    {
        var (uid, w, reject) = await BoundWorkdirAsync(http, db, repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }
        var workdir = w!;

        var full = WorkspaceFiles.ResolveInsideFinal(workdir, request.Path, out var error);
        if (full is null)
        {
            return Results.BadRequest(new { detail = error });
        }
        if (File.Exists(full))
        {
            return Results.Conflict(new { ok = false, error = "Já existe um arquivo nesse caminho." });
        }

        Directory.CreateDirectory(full);
        logger.LogInformation("workspace-file mkdir user={UserId} path={Path}",
            uid, WorkspaceFiles.RelativeOf(workdir, full));
        return Results.Ok(new { ok = true, path = WorkspaceFiles.RelativeOf(workdir, full) });
    }

    // ---------- POST /rename ----------

    private static async Task<IResult> RenameAsync(
        WorkspaceFileRenameRequest request, [FromQuery] string? chatId,
        HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        ILogger<Program> logger, CancellationToken ct)
    {
        var (uid, w, reject) = await BoundWorkdirAsync(http, db, repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }
        var workdir = w!;

        var from = WorkspaceFiles.ResolveInsideFinal(workdir, request.From, out var error);
        if (from is null)
        {
            return Results.BadRequest(new { detail = error });
        }
        var to = WorkspaceFiles.ResolveInsideFinal(workdir, request.To, out error);
        if (to is null)
        {
            return Results.BadRequest(new { detail = error });
        }
        if (IsRoot(workdir, from) || IsRoot(workdir, to))
        {
            return Results.BadRequest(new { detail = "A raiz do workspace não pode ser renomeada." });
        }
        if (!File.Exists(from) && !Directory.Exists(from))
        {
            return NotFoundResult();
        }
        if (File.Exists(to) || Directory.Exists(to))
        {
            return Results.Conflict(new { ok = false, error = "Destino já existe." });
        }
        if (Directory.Exists(from) &&
            to.StartsWith(from.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return Results.Conflict(new { ok = false, error = "Não é possível mover um diretório para dentro de si mesmo." });
        }

        var parent = Path.GetDirectoryName(to);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        if (File.Exists(from))
        {
            File.Move(from, to);
        }
        else
        {
            Directory.Move(from, to);
        }

        logger.LogInformation("workspace-file rename user={UserId} from={From} to={To}",
            uid, WorkspaceFiles.RelativeOf(workdir, from),
            WorkspaceFiles.RelativeOf(workdir, to));
        return Results.Ok(new { ok = true, path = WorkspaceFiles.RelativeOf(workdir, to) });
    }

    // ---------- POST /delete ----------

    private static async Task<IResult> DeleteAsync(
        WorkspaceFileDeleteRequest request, [FromQuery] string? chatId,
        HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        ILogger<Program> logger, CancellationToken ct)
    {
        var (uid, w, reject) = await BoundWorkdirAsync(http, db, repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }
        var workdir = w!;

        var full = WorkspaceFiles.ResolveInsideFinal(workdir, request.Path, out var error);
        if (full is null)
        {
            return Results.BadRequest(new { detail = error });
        }
        if (IsRoot(workdir, full))
        {
            return Results.BadRequest(new { detail = "A raiz do workspace não pode ser removida." });
        }

        try
        {
            if (File.Exists(full))
            {
                File.Delete(full);
            }
            else if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
            else
            {
                return NotFoundResult();
            }
        }
        catch (IOException)
        {
            return Results.Conflict(new { ok = false, error = "Não foi possível remover." });
        }

        logger.LogInformation("workspace-file delete user={UserId} path={Path}",
            uid, WorkspaceFiles.RelativeOf(workdir, full));
        return Results.Ok(new { ok = true, path = WorkspaceFiles.RelativeOf(workdir, full) });
    }

    /// <summary>True quando <paramref name="full"/> é a própria raiz do workdir.</summary>
    private static bool IsRoot(string workdir, string full) =>
        string.Equals(
            Path.GetFullPath(workdir).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(full).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.Ordinal);

    /// <summary>ETag opaco por mtime+size — invalida em qualquer escrita.</summary>
    private static string ETagOf(string full, long size) =>
        $"\"{File.GetLastWriteTimeUtc(full).Ticks:x}-{size:x}\"";
}

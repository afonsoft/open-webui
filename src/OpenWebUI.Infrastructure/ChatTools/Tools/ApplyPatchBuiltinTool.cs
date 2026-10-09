using System.Text;
using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:apply_patch</c> — patch multi-arquivo no formato OpenAI
/// (<c>*** Begin Patch</c> / <c>Add|Update|Delete File</c> /
/// <c>*** End Patch</c>) — SPEC-20261009-worktree-format-hooks RF-003.
/// Todos os paths passam pelo jail (<see cref="WorkspaceFiles.ResolveInside"/>,
/// incluindo o destino de <c>*** Move to:</c>); validação+pré-cálculo de
/// TODOS os arquivos antes de qualquer escrita — patch inválido não
/// aplica nada. Cada arquivo gera diff unificado persistido como
/// file_edit (array <c>files[]</c> no result). Depois roda o format
/// hook — falha vira warning, nunca erro. Mutável → gate de aprovação.
/// </summary>
/// <param name="formatHook">Format hook pós-patch (RF-004).</param>
public sealed class ApplyPatchBuiltinTool(FormatHookService? formatHook = null) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "apply_patch";

    /// <inheritdoc />
    public string Description =>
        "Apply a multi-file patch — call with the OpenAI apply_patch format "
        + "(*** Begin Patch / Add|Update|Delete File / *** End Patch).";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "patch": {
              "type": "string",
              "description": "OpenAI apply_patch text: '*** Begin Patch' … '*** Add File: p' (+lines) / '*** Update File: p' (optional '*** Move to: q'; '@@' separates hunks; ' ' context, '-' removed, '+' added) / '*** Delete File: p' … '*** End Patch'."
            }
          },
          "required": ["patch"]
        }
        """;

    /// <inheritdoc />
    public bool RequiresApproval => true;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var patch = args.TryGetProperty("patch", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(patch))
        {
            return new BuiltinToolResult("Parâmetro 'patch' é obrigatório.");
        }

        var ops = ApplyPatch.Parse(patch, out var error);
        if (ops is null)
        {
            return new BuiltinToolResult(error);
        }

        // Fase 1: jail + existência + pré-cálculo de cada arquivo.
        // Nada é escrito aqui — um patch inválido não aplica nada.
        var root = Path.GetFullPath(context.WorkspacePath);
        var plans = new List<FilePlan>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in ops)
        {
            var full = WorkspaceFiles.ResolveInside(context.WorkspacePath, op.Path, out var jailError);
            if (full is null)
            {
                return new BuiltinToolResult($"{op.Path}: {jailError}");
            }
            var rel = WorkspaceFiles.RelativeOf(root, full);
            if (!seen.Add(rel))
            {
                return new BuiltinToolResult($"Arquivo '{rel}' aparece 2× no patch.");
            }

            string? fullMove = null;
            string? relMove = null;
            if (op.MoveTo is not null)
            {
                fullMove = WorkspaceFiles.ResolveInside(context.WorkspacePath, op.MoveTo, out var moveError);
                if (fullMove is null)
                {
                    return new BuiltinToolResult($"{op.MoveTo}: {moveError}");
                }
                relMove = WorkspaceFiles.RelativeOf(root, fullMove);
            }

            var exists = File.Exists(full);
            string? oldText = exists ? await File.ReadAllTextAsync(full, ct) : null;
            string? newText;
            switch (op.Kind)
            {
                case "add":
                    if (exists)
                    {
                        return new BuiltinToolResult($"Add File '{rel}': arquivo já existe.");
                    }
                    if (Encoding.UTF8.GetByteCount(string.Join('\n', op.AddLines!)) > WorkspaceFiles.MaxFileBytes)
                    {
                        return new BuiltinToolResult(
                            $"Add File '{rel}': conteúdo excede {WorkspaceFiles.MaxFileBytes / 1024}KB.");
                    }
                    newText = string.Join('\n', op.AddLines!);
                    break;

                case "update":
                    if (!exists)
                    {
                        return new BuiltinToolResult($"Update File '{rel}': arquivo não existe.");
                    }
                    if (op.Hunks!.Count > 0)
                    {
                        newText = ApplyPatch.ApplyHunks(oldText!, op.Hunks, out var hunkError);
                        if (newText is null)
                        {
                            return new BuiltinToolResult($"Update File '{rel}': {hunkError}");
                        }
                    }
                    else
                    {
                        newText = oldText;
                    }
                    if (fullMove is not null && File.Exists(fullMove))
                    {
                        return new BuiltinToolResult($"Move to '{relMove}': destino já existe.");
                    }
                    if (Encoding.UTF8.GetByteCount(newText) > WorkspaceFiles.MaxFileBytes)
                    {
                        return new BuiltinToolResult(
                            $"Update File '{rel}': resultado excede {WorkspaceFiles.MaxFileBytes / 1024}KB.");
                    }
                    break;

                case "delete":
                    if (!exists)
                    {
                        return new BuiltinToolResult($"Delete File '{rel}': arquivo não existe.");
                    }
                    newText = null;
                    break;

                default:
                    return new BuiltinToolResult($"Operação desconhecida '{op.Kind}'.");
            }

            plans.Add(new FilePlan(op.Kind, rel, full, relMove, fullMove, oldText, newText));
        }

        // Fase 2: escrita/movimento/remoção.
        var results = new List<(string Path, string Action, UnifiedDiff.Result Diff)>();
        foreach (var plan in plans)
        {
            switch (plan.Kind)
            {
                case "add":
                    Directory.CreateDirectory(Path.GetDirectoryName(plan.Full)!);
                    await File.WriteAllTextAsync(plan.Full, plan.NewText!, ct);
                    results.Add((plan.Rel, "add", UnifiedDiff.Compute(plan.Rel, null, plan.NewText!)));
                    break;

                case "update":
                    if (plan.FullMove is not null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(plan.FullMove)!);
                        File.Move(plan.Full, plan.FullMove);
                    }
                    if (plan.NewText != plan.OldText)
                    {
                        await File.WriteAllTextAsync(plan.FullMove ?? plan.Full, plan.NewText!, ct);
                    }
                    results.Add((plan.RelMove ?? plan.Rel,
                        plan.FullMove is not null ? "rename+update" : "update",
                        UnifiedDiff.Compute(
                            plan.RelMove ?? plan.Rel, plan.OldText, plan.NewText!)));
                    break;

                case "delete":
                    File.Delete(plan.Full);
                    results.Add((plan.Rel, "delete",
                        UnifiedDiff.Compute(plan.Rel, plan.OldText, "")));
                    break;
            }
        }

        // Format hook uma vez com todos os arquivos tocados (RF-004).
        var formatWarning = formatHook is null ? null : await formatHook.RunForUserAsync(
            context.UserId, context.WorkspacePath,
            results.Select(r => r.Path).ToList(), ct);

        var sb = new StringBuilder($"Patch aplicado: {results.Count} arquivo(s).");
        foreach (var (path, action, diff) in results)
        {
            sb.Append($"\n{action}: {path} (+{diff.Added}/-{diff.Removed})");
        }
        if (formatWarning is not null)
        {
            sb.Append($"\n[format] {formatWarning}");
        }

        return new BuiltinToolResult(sb.ToString(), new
        {
            files = results.Select(r => new
            {
                path = r.Path,
                action = r.Action,
                added = r.Diff.Added,
                removed = r.Diff.Removed,
                diff = FileWriteBuiltinTool.Truncate(r.Diff.Text, 8192),
            }).ToList(),
            filesChanged = results.Count,
            formatWarning,
        });
    }

    /// <summary>Plano de um arquivo depois da validação (fase 1).</summary>
    private sealed record FilePlan(
        string Kind, string Rel, string Full,
        string? RelMove, string? FullMove,
        string? OldText, string? NewText);
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:delegate_task</c> — delega uma subtarefa a uma run filha
/// (SPEC-20261007-chat-agent-parity RF-016, padrão <c>task</c> do opencode /
/// child session do Devin): cria um chat próprio do usuário com o prompt
/// delegado, enfileira a run (histórico isolado herdando modelo, tools —
/// menos o próprio delegate, para impedir recursão — e preset do chat pai)
/// e aguarda o término até <see cref="TimeoutSeconds"/>; o resultado volta
/// ao transcript do pai como tool_result com o link do chat filho.
/// Um chat separado é necessário: o dispatcher serializa runs por chat —
/// um filho no mesmo chat só executaria depois que o pai terminasse,
/// enquanto o pai o aguarda (deadlock).
/// <para>
/// SPEC-20261010-async-delegate: <c>wait=false</c> retorna imediatamente
/// com <c>childRunId</c> — a run filha continua server-side e o pai
/// colhe o resultado depois via <c>builtin:run_result</c> (paralelismo
/// real entre subtarefas). A profundidade é derivada da cadeia
/// <see cref="ChatRun.ParentRunId"/>: runs com profundidade ≥
/// <see cref="MaxDepth"/> perdem a tool <c>delegate_task</c> (sub-agents
/// encadeados até 3 níveis, estilo opencode-web).
/// </para>
/// </summary>
public sealed class DelegateTaskBuiltinTool(
    IServiceScopeFactory scopeFactory,
    IChatRunDispatcher dispatcher) : IBuiltinChatTool
{
    /// <summary>Tempo máximo de espera pela run filha (SPEC: 5min).</summary>
    private const int TimeoutSeconds = 300;

    /// <summary>Intervalo entre polls de status da run filha.</summary>
    private const int PollMs = 1500;

    /// <summary>
    /// Profundidade máxima da cadeia de delegação (SPEC-20261010-async-delegate):
    /// depth 0 = run raiz; filhos a partir de <see cref="MaxDepth"/> não herdam
    /// a tool <c>delegate_task</c>. Derivada caminhando <see cref="ChatRun.ParentRunId"/>
    /// — sem coluna nova no schema.
    /// </summary>
    private const int MaxDepth = 3;

    /// <summary>Caps de entrada/saída.</summary>
    private const int MaxPromptChars = 4000;
    private const int MaxContextChars = 2000;
    private const int MaxResultChars = 6000;
    private const int MaxTitleChars = 60;

    /// <inheritdoc />
    public string Name => "delegate_task";

    /// <inheritdoc />
    public string Description =>
        "Delegate to a child agent — call for parallel work. "
            + "Set wait=false to run in background and collect later via builtin_run_result.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "prompt": {
              "type": "string",
              "description": "Complete, self-contained instruction for the subtask (the child cannot see this conversation)."
            },
            "context": {
              "type": "string",
              "description": "Optional extra context (paths, decisions, constraints) appended to the prompt."
            },
            "wait": {
              "type": "boolean",
              "description": "true (default): block until the child finishes. false: return childRunId immediately — collect later with builtin_run_result."
            }
          },
          "required": ["prompt"]
        }
        """;

    /// <inheritdoc />
    public bool RequiresApproval => true;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var prompt = ReadString(args, "prompt", MaxPromptChars);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new BuiltinToolResult("Parâmetro 'prompt' é obrigatório.");
        }

        var extra = ReadString(args, "context", MaxContextChars);
        var wait = !args.TryGetProperty("wait", out var waitEl)
            || waitEl.ValueKind is not JsonValueKind.False;
        var fullPrompt = string.IsNullOrWhiteSpace(extra)
            ? prompt
            : $"{prompt}\n\nContexto adicional:\n{extra}";

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var parentRun = context.RunId is { } parentId
            ? await db.ChatRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == parentId, ct)
            : null;
        if (parentRun is null)
        {
            return new BuiltinToolResult(
                "delegate_task só pode ser chamada dentro de uma run de chat.");
        }

        // Profundidade da cadeia pai (depth 0 = raiz). Runs de profundidade
        // ≥ MaxDepth perdem a tool delegate_task — o filho não pode delegar
        // além do limite (SPEC-20261010-async-delegate).
        var childDepth = await DepthOfAsync(db, parentRun, ct) + 1;

        // Herda modelo + tools do pai; exclui o próprio delegate apenas
        // quando o filho atingiria o teto de profundidade.
        IReadOnlyList<string>? childToolIds = null;
        try
        {
            var parentRequest = JsonSerializer.Deserialize<ChatCompletionRequest>(
                parentRun.RequestJson, JsonOptions);
            childToolIds = parentRequest?.ToolIds
                ?.Where(id => childDepth < MaxDepth
                    || !id.Equals(
                        $"{BuiltinToolRegistry.IdPrefix}{Name}",
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (JsonException)
        {
            // RequestJson corrompido: delega sem tools, nunca falha por isso.
        }

        var preset = context.ChatId is { } chatId
            ? await db.Chats.AsNoTracking()
                .Where(c => c.Id == chatId)
                .Select(c => c.ApprovalPreset)
                .FirstOrDefaultAsync(ct) ?? "approve-mutations"
            : "approve-mutations";

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var title = prompt.Length > MaxTitleChars
            ? prompt[..MaxTitleChars] + "…"
            : prompt;
        var childChat = new Chat
        {
            UserId = context.UserId,
            Title = $"delegado: {title}",
            ParentChatId = context.ChatId,
            ModelsJson = JsonSerializer.Serialize(new[] { parentRun.Model }),
            ToolIdsJson = JsonSerializer.Serialize(childToolIds ?? []),
            ApprovalPreset = preset,
            CreatedAt = now,
            UpdatedAt = now,
        };
        childChat.Messages.Add(new ChatMessage
        {
            ChatId = childChat.Id,
            Role = "user",
            Content = fullPrompt,
            Position = 0,
            Timestamp = now,
        });
        db.Chats.Add(childChat);

        var childRequest = new ChatCompletionRequest(
            parentRun.Model,
            [new ChatCompletionMessage("user", fullPrompt)],
            Stream: true,
            ToolIds: childToolIds);
        var childRun = new ChatRun
        {
            ChatId = childChat.Id,
            UserId = context.UserId,
            Model = parentRun.Model,
            RequestJson = JsonSerializer.Serialize(childRequest, JsonOptions),
            ParentRunId = parentRun.Id,
            CreatedAt = now,
        };
        db.ChatRuns.Add(childRun);
        await db.SaveChangesAsync(ct);
        dispatcher.Enqueue(childRun.Id);

        var link = $"/c/{childChat.Id}";
        if (!wait)
        {
            // wait=false: devolve já — o pai segue o turno e colhe o
            // resultado depois via builtin:run_result (paralelismo real).
            return new BuiltinToolResult(
                $"Subtarefa delegada em background — run filha {childRun.Id} "
                    + $"(chat filho: {link}). Chame builtin_run_result com "
                    + $"run_id={childRun.Id} para colher o resultado.",
                new
                {
                    childChatId = childChat.Id,
                    childRunId = childRun.Id,
                    status = "queued",
                });
        }
        var deadline = DateTimeOffset.UtcNow.AddSeconds(TimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollMs, ct);
            var status = await db.ChatRuns.AsNoTracking()
                .Where(r => r.Id == childRun.Id)
                .Select(r => new { r.Status, r.PartialContent, r.Error })
                .FirstOrDefaultAsync(ct);
            if (status is null || status.Status
                    is ChatRunStatus.Queued or ChatRunStatus.Running or ChatRunStatus.Paused)
            {
                continue;
            }

            var result = new
            {
                childChatId = childChat.Id,
                childRunId = childRun.Id,
                status = status.Status,
            };
            return status.Status switch
            {
                ChatRunStatus.Completed => new BuiltinToolResult(
                    $"{Truncate(status.PartialContent ?? "(sem conteúdo)", MaxResultChars)}\n\n"
                        + $"(subtarefa concluída — chat filho: {link})",
                    result),
                _ => new BuiltinToolResult(
                    $"Subtarefa terminou como '{status.Status}'"
                        + (status.Error is { } e ? $": {Truncate(e, 500)}" : string.Empty)
                        + $" — chat filho: {link}",
                    result),
            };
        }

        return new BuiltinToolResult(
            $"Subtarefa ainda em andamento após {TimeoutSeconds}s — continua em "
                + $"background; acompanhe o resultado no chat filho: {link}",
            new { childChatId = childChat.Id, childRunId = childRun.Id, status = "running" });
    }

    /// <summary>
    /// Profundidade da run na cadeia <see cref="ChatRun.ParentRunId"/>
    /// (0 = raiz). Limita o walk a <see cref="MaxDepth"/> — ciclos ou
    /// correntes longas não custam mais que MaxDepth lookups.
    /// </summary>
    private static async Task<int> DepthOfAsync(
        AppDbContext db, ChatRun run, CancellationToken ct)
    {
        var depth = 0;
        var cursor = run.ParentRunId;
        while (cursor is not null && depth < MaxDepth)
        {
            depth++;
            cursor = await db.ChatRuns.AsNoTracking()
                .Where(r => r.Id == cursor)
                .Select(r => r.ParentRunId)
                .FirstOrDefaultAsync(ct);
        }
        return depth;
    }

    private static string? ReadString(JsonElement args, string field, int max)
    {
        if (!args.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.String
            || el.GetString() is not { } s)
        {
            return null;
        }
        return s.Length > max ? s[..max] : s;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
}

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Completions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Runs;

/// <summary>
/// Executa uma run desacoplada (SPEC-20261007-chat-detached-runs): roda o
/// mesmo pipeline de <c>/api/chat/completions</c> (enriquecimento, arena,
/// pipelines, tools, stream) publicando os eventos no
/// <see cref="ChatRunBroadcaster"/> e persistindo checkpoints por iteração
/// em <see cref="ChatRun.PartialContent"/>. Ao final grava a mensagem do
/// assistant no chat — a persistência deixa de depender do cliente.
/// </summary>
public sealed class ChatRunExecutor(
    AppDbContext db,
    ConfigService config,
    ProviderService providers,
    RagService rag,
    ToolExecutor toolExecutor,
    PipelineClientService pipelines,
    WebSearchService webSearch,
    ChatRunBroadcaster broadcaster,
    ChatRunApprovals approvals,
    ChatRunPauses pauses,
    IWebHostEnvironment env,
    ILogger<ChatRunExecutor> logger)
{
    /// <summary>Bytes de delta acumulados antes de gravar um checkpoint.</summary>
    private const int CheckpointBytes = 2048;

    /// <summary>Tamanho máximo do preview de args/resultado publicado no SSE.</summary>
    private const int PreviewChars = 2048;

    /// <summary>Tamanho máximo de um diff publicado no evento <c>changes</c>.</summary>
    private const int ChangeDiffChars = 8192;

    /// <summary>
    /// Arquivos alterados pela run (RF-015): acumulado por path a cada
    /// file_write/file_edit — alimenta a aba Changes do painel.
    /// </summary>
    private readonly Dictionary<string, RunChangeItem> _changes = new();

    /// <summary>Roda a run até o fim e atualiza o registro com o resultado.</summary>
    public async Task ExecuteAsync(ChatRun run, CancellationToken ct)
    {
        ChatCompletionRequest request;
        try
        {
            request = JsonSerializer.Deserialize<ChatCompletionRequest>(
                run.RequestJson, JsonOptions)!;
        }
        catch (JsonException ex)
        {
            await FailAsync(run, $"Request de run inválido: {ex.Message}", ct);
            return;
        }

        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == run.UserId, ct);
        if (user is null)
        {
            await FailAsync(run, "Usuário da run não encontrado.", ct);
            return;
        }

        var content = new StringBuilder();
        var sinceCheckpoint = 0;
        IReadOnlyList<ChatCompletionMessage>? toolMessages = null;

        try
        {
            // Modelos pipeline:{id} → roteia ao servidor de pipelines e repassa as linhas.
            if (request.Model.StartsWith("pipeline:", StringComparison.Ordinal))
            {
                await RunPipelineAsync(request, run, ct);
                // [DONE] só depois do FinishAsync — ver caminho principal.
                await FinishAsync(run, string.Empty, ct);
                broadcaster.Publish(run.Id, "data: [DONE]");
                return;
            }

            // Arena gera as duas respostas e publica o payload {"arena":...}.
            var arenaModel = request.Model.StartsWith("arena:", StringComparison.Ordinal)
                ? request.Model["arena:".Length..]
                : request.Model;
            if (await ChatPipeline.TryRunArenaAsync(
                request, arenaModel, user, db, config, rag, providers, webSearch,
                line =>
                {
                    // O [DONE] do arena é retido: sai depois do FinishAsync.
                    if (line != "data: [DONE]")
                    {
                        broadcaster.Publish(run.Id, line);
                    }
                    return Task.CompletedTask;
                },
                ct))
            {
                await FinishAsync(run, string.Empty, ct);
                broadcaster.Publish(run.Id, "data: [DONE]");
                return;
            }

            var effective = await ChatPipeline.EnrichRequestAsync(
                request, user, db, config, rag, webSearch, ct);

            var outletRules = ModelFilterService.OutletRules(
                ModelFilterService.Parse(await db.ModelEntries.AsNoTracking()
                    .Where(m => m.IsActive && (m.Id == request.Model || m.Name == request.Model)
                        && (m.UserId == user.Id || m.UserId == "public"))
                    .Select(m => m.MetaJson).FirstOrDefaultAsync(ct)));

            var tools = request.ToolIds is { Count: > 0 }
                ? await toolExecutor.LoadEnabledAsync(user.Id, request.ToolIds, ct)
                : null;
            if (tools is { Count: > 0 })
            {
                effective = effective with
                {
                    Tools = tools
                        .Select(t => JsonSerializer.Deserialize<JsonElement>(t.SpecJson))
                        .ToList(),
                };
                var callbacks = new ChatPipeline.ToolLoopCallbacks(
                    // Checkpoint de pausa em cada iteração do loop (RF-013):
                    // fase nova só começa depois do resume.
                    OnPhaseAsync: async (phase, label, t) =>
                    {
                        await pauses.WaitIfPausedAsync(run.Id, t);
                        await PublishPhaseAsync(run, phase, label);
                    },
                    OnCallAsync: (call, t) => PublishToolCallAsync(run, call),
                    GateAsync: (call, t) => GateToolCallAsync(run, tools, call, t),
                    OnResultAsync: (call, output, result, denied, t)
                        => PublishToolResultAsync(run, call, output, result, denied),
                    // Texto do modelo sai delta a delta — sem isso a resposta
                    // inteira só aparecia num blob no fim da run.
                    OnDeltaAsync: async (piece, t) =>
                    {
                        await pauses.WaitIfPausedAsync(run.Id, t);
                        var processed = piece;
                        foreach (var (regex, replacement) in outletRules)
                        {
                            processed = regex.Replace(processed, replacement);
                        }
                        if (processed.Length == 0)
                        {
                            return;
                        }
                        var chunk = JsonSerializer.Serialize(new
                        {
                            choices = new[] { new { index = 0, delta = new { content = processed } } },
                        });
                        broadcaster.Publish(run.Id, $"data: {chunk}");
                        content.Append(processed);
                        sinceCheckpoint += processed.Length;
                        if (sinceCheckpoint >= CheckpointBytes)
                        {
                            run.PartialContent = content.ToString();
                            await db.SaveChangesAsync(CancellationToken.None);
                            sinceCheckpoint = 0;
                        }
                    });
                var builtinContext = BuildToolContext(run, user);
                var outcome = await ChatPipeline.RunToolLoopAsync(
                    effective, tools, toolExecutor, providers, ct, callbacks, builtinContext);
                toolMessages = outcome?.ToolMessages;
            }
            else
            {
                await foreach (var line in providers.StreamCompletionAsync(effective, ct))
                {
                    await pauses.WaitIfPausedAsync(run.Id, ct);
                    var processed = ModelFilterService.ProcessSseLine(line, outletRules);
                    broadcaster.Publish(run.Id, processed);
                    AccumulateDelta(processed, content);
                    sinceCheckpoint += processed.Length;
                    if (sinceCheckpoint >= CheckpointBytes)
                    {
                        run.PartialContent = content.ToString();
                        await db.SaveChangesAsync(CancellationToken.None);
                        sinceCheckpoint = 0;
                    }
                }
            }

            // [DONE] depois do FinishAsync: o cliente recarrega o chat ao ver o
            // marcador — a mensagem precisa já estar persistida.
            await FinishAsync(run, content.ToString(), ct, toolMessages: toolMessages);
            broadcaster.Publish(run.Id, "data: [DONE]");
        }
        catch (OperationCanceledException)
        {
            approvals.Cancel(run.Id);
            await FinishAsync(run, content.ToString(), ct, ChatRunStatus.Stopped, toolMessages);
            broadcaster.Publish(run.Id, "data: [DONE]");
        }
        catch (Exception ex)
        {
            // Qualquer falha fecha a run como failed — uma exceção que escapasse
            // deixaria a run eternamente "running" (o dispatcher só loga).
            if (ex is not (InvalidOperationException or HttpRequestException or TaskCanceledException))
            {
                logger.LogError(ex, "Run {RunId} falhou com exceção inesperada.", run.Id);
            }

            var message = ex is HttpRequestException or TaskCanceledException
                ? $"Falha ao contactar o provedor: {ex.Message}"
                : ex.Message;
            approvals.Cancel(run.Id);
            broadcaster.Publish(run.Id, $"data: {JsonSerializer.Serialize(new { error = message })}");
            run.Error = message;
            await FinishAsync(run, content.ToString(), ct, ChatRunStatus.Failed, toolMessages);
            broadcaster.Publish(run.Id, "data: [DONE]");
        }
    }

    /// <summary>Publica o evento <c>status</c> de fase no stream da run.</summary>
    private Task PublishPhaseAsync(ChatRun run, string phase, string? label)
    {
        broadcaster.Publish(run.Id,
            $"event: status\ndata: {JsonSerializer.Serialize(new RunPhaseEvent(phase, label), JsonOptions)}");
        return Task.CompletedTask;
    }

    /// <summary>Publica o evento <c>tool_call</c> com preview higienizado dos args.</summary>
    private Task PublishToolCallAsync(ChatRun run, ProviderToolCall call)
    {
        broadcaster.Publish(run.Id,
            $"event: tool_call\ndata: {JsonSerializer.Serialize(new RunToolCallEvent(call.Id, call.Name, Scrub(Truncate(call.ArgumentsJson, PreviewChars))), JsonOptions)}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Contexto das tools built-in da run (SPEC-20261007-chat-agent-tools):
    /// workspace confinado em <c>data/workspaces/{userId}</c> e uploads em
    /// <c>data/uploads/{userId}</c> (mesma raiz das telas de Imagens).
    /// </summary>
    private BuiltinToolContext BuildToolContext(ChatRun run, User user) => new(
        user.Id,
        run.ChatId,
        run.Id,
        Path.Join(env.ContentRootPath, "data", "workspaces", user.Id),
        Path.Join(env.ContentRootPath, "data", "uploads", user.Id));

    /// <summary>Publica o evento <c>tool_result</c> (ok=false em erro/negação).</summary>
    private Task PublishToolResultAsync(
        ChatRun run, ProviderToolCall call, string output, JsonElement? result, bool denied)
    {
        var ok = !denied && !output.StartsWith("Erro", StringComparison.Ordinal);
        string? imagePath = null;
        string? videoPath = null;
        if (result is { } el && el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("imagePath", out var img))
            {
                imagePath = img.GetString();
            }
            if (el.TryGetProperty("videoPath", out var vid))
            {
                videoPath = vid.GetString();
            }
        }

        // todo_write publica o snapshot de tarefas como evento `tasks`
        // (RF-010 chat-agent-parity) — replay cobre attach tardio.
        if (result is { } res && res.ValueKind == JsonValueKind.Object
            && res.TryGetProperty("tasks", out var tasks)
            && tasks.ValueKind == JsonValueKind.Array)
        {
            broadcaster.Publish(run.Id, $"event: tasks\ndata: {tasks.GetRawText()}");
        }

        // file_write/file_edit acumulam o diff por path e publicam o
        // snapshot `changes` (RF-015) — aba Changes do painel lateral.
        if (result is { } chg && chg.ValueKind == JsonValueKind.Object
            && chg.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
            && chg.TryGetProperty("added", out var a) && a.ValueKind == JsonValueKind.Number
            && chg.TryGetProperty("removed", out var r) && r.ValueKind == JsonValueKind.Number)
        {
            var diff = chg.TryGetProperty("diff", out var d) && d.ValueKind == JsonValueKind.String
                ? Truncate(d.GetString()!, ChangeDiffChars)
                : null;
            _changes[p.GetString()!] = new RunChangeItem(
                p.GetString()!, a.GetInt32(), r.GetInt32(), diff);
            broadcaster.Publish(run.Id,
                $"event: changes\ndata: {JsonSerializer.Serialize(new RunChangesEvent(_changes.Values.ToList()), JsonOptions)}");
        }
        broadcaster.Publish(run.Id,
            $"event: tool_result\ndata: {JsonSerializer.Serialize(new RunToolResultEvent(call.Id, call.Name, ok, Scrub(Truncate(output, PreviewChars)), ImagePath: imagePath, Denied: denied, Result: result, VideoPath: videoPath), JsonOptions)}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gate de aprovação por tool call (RF-003/RF-004): tool não-mutável
    /// ou já lembrada executa direto; preset <c>always-allow</c> libera,
    /// <c>allow-readonly</c> nega; <c>approve-mutations</c> emite
    /// <c>approval_asked</c> e espera a decisão do dono (timeout → deny);
    /// uma negação pode carregar a instrução do dono (RF-002 chat-agent-ux).
    /// O preset é relido a cada call — mudança mid-run vale já no próximo.
    /// </summary>
    private async Task<ToolGateDecision> GateToolCallAsync(
        ChatRun run, IReadOnlyList<Tool> tools, ProviderToolCall call, CancellationToken ct)
    {
        // Checkpoint de pausa antes do gate (RF-013) — a call pendente
        // só resolve depois do resume.
        await pauses.WaitIfPausedAsync(run.Id, ct);

        // ask_user (RF-005): sempre pausa — a pergunta é o próprio gate;
        // não passa por preset nem por remembered. A resposta volta como
        // ToolGateDecision.Output e vira o resultado da tool.
        if (string.Equals(call.Name, "builtin:ask_user", StringComparison.OrdinalIgnoreCase))
        {
            return await AskUserAsync(run, call, ct);
        }

        var tool = tools.FirstOrDefault(t => ToolExecutor.FunctionName(t) == call.Name);
        if (tool is null || !ToolExecutor.IsMutable(tool)
            || approvals.IsRemembered(run.ChatId, call.Name))
        {
            return ToolGateDecision.Allow;
        }

        var preset = await db.Chats.AsNoTracking()
            .Where(c => c.Id == run.ChatId)
            .Select(c => c.ApprovalPreset)
            .FirstOrDefaultAsync(ct)
            ?? "approve-mutations";
        switch (preset)
        {
            case "always-allow":
                return ToolGateDecision.Allow;
            case "allow-readonly":
                return ToolGateDecision.Deny;
            case "auto":
            case "smart":
            {
                // RF-012 (paridade OpenHands): LOW executa direto; MEDIUM
                // executa com notice no stream; HIGH cai no fluxo de
                // pergunta abaixo. O preset <c>smart</c> segue o mesmo
                // tiers, mas mutações de arquivo caem na pergunta —
                // "executa sem aprovação, com restrição em alterações".
                var workspace = Path.Join(
                    env.ContentRootPath, "data", "workspaces", run.UserId);
                var risk = ToolCallRiskClassifier.Classify(
                    tool, call.ArgumentsJson, workspace);
                var fileMutation = risk == ToolCallRisk.Medium
                    && preset == "smart"
                    && ToolCallRiskClassifier.IsFileMutation(
                        tool, call.ArgumentsJson, workspace);
                if (risk == ToolCallRisk.Low)
                {
                    return ToolGateDecision.Allow;
                }

                if (risk == ToolCallRisk.Medium && !fileMutation)
                {
                    broadcaster.Publish(run.Id,
                        $"event: status\ndata: {JsonSerializer.Serialize(new RunPhaseEvent("auto_approved", call.Name), JsonOptions)}");
                    return ToolGateDecision.Allow;
                }

                break;
            }
        }

        var argsPreview = Scrub(Truncate(call.ArgumentsJson, PreviewChars));
        var kind = !string.IsNullOrWhiteSpace(tool.Code)
            ? "python"
            : tool.Url.StartsWith(McpClientService.VirtualUrlPrefix,
                StringComparison.Ordinal) ? "mcp" : "http";
        broadcaster.Publish(run.Id,
            $"event: status\ndata: {JsonSerializer.Serialize(new RunPhaseEvent("awaiting_approval", call.Name), JsonOptions)}");
        broadcaster.Publish(run.Id,
            $"event: approval_asked\ndata: {JsonSerializer.Serialize(new RunApprovalAskedEvent(call.Id, call.Name, kind, argsPreview), JsonOptions)}");
        var result = await approvals.WaitAsync(run.Id, run.ChatId, call.Id, call.Name, ct);
        var message = string.IsNullOrWhiteSpace(result.Message)
            ? null
            : Scrub(Truncate(result.Message!, PreviewChars));
        return new ToolGateDecision(result.Approved, message);
    }

    /// <summary>
    /// Gate do <c>builtin:ask_user</c> (RF-005): publica
    /// <c>question_asked</c> com a pergunta e opções dos args, espera a
    /// decisão do dono pelo mesmo mecanismo de aprovação e devolve a
    /// resposta como <see cref="ToolGateDecision.Output"/> — a tool não
    /// executa de fato. Negar/pular → "o usuário não respondeu" (a run
    /// segue; o modelo decide o próximo passo).
    /// </summary>
    private async Task<ToolGateDecision> AskUserAsync(
        ChatRun run, ProviderToolCall call, CancellationToken ct)
    {
        var question = "?";
        string[] options = [];
        var multiple = false;
        try
        {
            var args = JsonDocument.Parse(call.ArgumentsJson).RootElement;
            question = args.TryGetProperty("question", out var q) && q.GetString() is { Length: > 0 } qt
                ? qt
                : question;
            if (args.TryGetProperty("options", out var opts)
                && opts.ValueKind == JsonValueKind.Array)
            {
                options = [.. opts.EnumerateArray()
                    .Select(o => o.GetString())
                    .Where(o => !string.IsNullOrWhiteSpace(o))
                    .Select(o => o!)
                    .Take(6)];
            }
            multiple = args.TryGetProperty("multiple", out var m)
                && m.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return new ToolGateDecision(true, Output: "Pergunta malformada (args inválidos).");
        }

        broadcaster.Publish(run.Id,
            $"event: status\ndata: {JsonSerializer.Serialize(new RunPhaseEvent("awaiting_approval", call.Name), JsonOptions)}");
        broadcaster.Publish(run.Id,
            $"event: question_asked\ndata: {JsonSerializer.Serialize(new RunQuestionAskedEvent(call.Id, Scrub(Truncate(question, PreviewChars)), options, multiple), JsonOptions)}");

        var result = await approvals.WaitAsync(run.Id, run.ChatId, call.Id, call.Name, ct);
        var answer = result.Approved && !string.IsNullOrWhiteSpace(result.Message)
            ? Scrub(Truncate(result.Message!, PreviewChars))
            : "O usuário não respondeu.";
        return new ToolGateDecision(true, Output: answer);
    }

    /// <summary>Esconde padrões óbvios de secret antes de publicar no SSE.</summary>
    private static string Scrub(string value)
    {
        var scrubbed = SecretPattern.Replace(value, m
            => m.Value.StartsWith("Bearer", StringComparison.Ordinal)
                ? "Bearer ***"
                : m.Value[..3] + "***");
        return scrubbed;
    }

    private static readonly System.Text.RegularExpressions.Regex SecretPattern = new(
        @"Bearer\s+[^\s""']+|sk-[A-Za-z0-9_-]{8,}",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Roteia `pipeline:{id}` e repassa as linhas SSE do upstream.</summary>
    private async Task RunPipelineAsync(
        ChatCompletionRequest request, ChatRun run, CancellationToken ct)
    {
        var pipeId = request.Model["pipeline:".Length..];
        var (server, body, valvesJson) = await ChatPipeline.PreparePipelineRouteAsync(
            request, db, pipelines, ct);
        if (server is null)
        {
            broadcaster.Publish(run.Id, $"data: {JsonSerializer.Serialize(new { error = $"Pipe '{pipeId}' não encontrado." })}");
            return;
        }

        var proxied = await pipelines.RouteCompletionAsync(server, body, valvesJson, ct);
        if (proxied.Response is null)
        {
            broadcaster.Publish(run.Id, $"data: {JsonSerializer.Serialize(new { error = $"Falha no servidor de pipelines: {proxied.Error}" })}");
            return;
        }

        using var upstream = proxied.Response;
        if (!upstream.IsSuccessStatusCode)
        {
            var body_ = await upstream.Content.ReadAsStringAsync(ct);
            broadcaster.Publish(run.Id, $"data: {JsonSerializer.Serialize(new { error = $"Pipeline devolveu {(int)upstream.StatusCode}: {Truncate(body_, 500)}" })}");
            return;
        }

        using var reader = new StreamReader(await upstream.Content.ReadAsStreamAsync(ct));
        var accumulated = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            broadcaster.Publish(run.Id, line);
            AccumulateDelta(line, accumulated);
        }

        run.PartialContent = accumulated.ToString();
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Extrai o delta de texto de uma linha SSE `data: {...}`.</summary>
    private static void AccumulateDelta(string line, StringBuilder into)
    {
        if (!line.StartsWith("data: ", StringComparison.Ordinal) || line == "data: [DONE]")
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(line["data: ".Length..]);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var delta = choices[0].TryGetProperty("delta", out var d) ? d : default;
                var piece = delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("content", out var c)
                        ? c.GetString()
                        : null;
                if (piece is not null)
                {
                    into.Append(piece);
                }
            }
        }
        catch (JsonException)
        {
            // Linha não-JSON (keep-alive etc.): ignora.
        }
    }

    /// <summary>
    /// Grava as mensagens do loop de tools (assistant com ToolCallsJson +
    /// respostas role=tool) e a mensagem final do assistant no chat, e
    /// finaliza a run.
    /// </summary>
    private async Task FinishAsync(
        ChatRun run, string content, CancellationToken ct, string? status = null,
        IReadOnlyList<ChatCompletionMessage>? toolMessages = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        run.Status = status ?? ChatRunStatus.Completed;
        run.CompletedAt = now;
        run.PartialContent = content;

        if (content.Length > 0 || toolMessages is { Count: > 0 })
        {
            var chat = await db.Chats.Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == run.ChatId, CancellationToken.None);
            if (chat is not null)
            {
                var nextPosition = chat.Messages.Count == 0
                    ? 0
                    : chat.Messages.Max(m => m.Position) + 1;
                foreach (var m in toolMessages ?? [])
                {
                    chat.Messages.Add(new ChatMessage
                    {
                        ChatId = chat.Id,
                        Role = m.Role,
                        Content = m.Content,
                        Model = m.Role == "assistant" ? run.Model : null,
                        ToolCallsJson = m.ToolCallsJson,
                        ToolCallId = m.ToolCallId,
                        Position = nextPosition++,
                        Timestamp = now,
                    });
                }
                if (content.Length > 0)
                {
                    chat.Messages.Add(new ChatMessage
                    {
                        ChatId = chat.Id,
                        Role = "assistant",
                        Content = content,
                        Model = run.Model,
                        Position = nextPosition,
                        Timestamp = now,
                    });
                }
                chat.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(CancellationToken.None);
        broadcaster.Publish(
            run.Id,
            $"event: status\ndata: {JsonSerializer.Serialize(new { status = run.Status })}");
    }

    /// <summary>Marca a run como failed antes de iniciar.</summary>
    private async Task FailAsync(ChatRun run, string error, CancellationToken ct)
    {
        logger.LogWarning("Run {RunId} falhou antes de executar: {Error}", run.Id, error);
        run.Status = ChatRunStatus.Failed;
        run.Error = error;
        run.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(CancellationToken.None);
        broadcaster.Publish(run.Id, $"data: {JsonSerializer.Serialize(new { error })}");
        broadcaster.Publish(run.Id, "data: [DONE]");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Endpoints;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Completions;

/// <summary>
/// Pipeline compartilhado de completions: enriquecimento do request
/// (modelo custom, arquivos, web search, memórias, filtros), loop de tools
/// e roteamento arena/pipeline. Extraído de <see cref="ApiEndpoints"/> para
/// ser reusado pelo executor de runs desacopladas
/// (SPEC-20261007-chat-detached-runs).
/// </summary>
public static class ChatPipeline
{
    /// <summary>Aplica modelo personalizado, contexto de arquivos e memórias à requisição.</summary>
    public static async Task<ChatCompletionRequest> EnrichRequestAsync(
        ChatCompletionRequest request,
        User user,
        AppDbContext db,
        ConfigService config,
        RagService rag,
        WebSearchService webSearch,
        CancellationToken ct)
    {
        var model = request.Model;
        var messages = request.Messages.ToList();
        var parameters = request.Params?.ToDictionary(kv => kv.Key, kv => kv.Value);
        var systemParts = new List<string>();

        // 1. Modelo personalizado do workspace → redireciona para o modelo base e aplica config.
        var customModel = await db.ModelEntries.AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.IsActive && (m.Id == model || m.Name == model)
                    && (m.UserId == user.Id || m.UserId == "public"), ct);
        if (customModel is not null
            && !await ModelEndpoints.HasModelAccessAsync(user, customModel, db, ct))
        {
            customModel = null;
        }
        if (customModel is not null)
        {
            if (!string.IsNullOrWhiteSpace(customModel.BaseModelId))
            {
                model = customModel.BaseModelId;
            }
            if (!string.IsNullOrWhiteSpace(customModel.SystemPrompt))
            {
                systemParts.Add(customModel.SystemPrompt);
            }

            if (!string.IsNullOrWhiteSpace(customModel.ParamsJson))
            {
                try
                {
                    var customParams = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        customModel.ParamsJson);
                    if (customParams is not null)
                    {
                        parameters ??= [];
                        foreach (var (key, value) in customParams)
                        {
                            parameters.TryAdd(key, value);
                        }
                    }
                }
                catch (JsonException)
                {
                }
            }
        }

        // 1.5. Skills anexadas ao modelo custom (MetaJson.skill_ids) → system prompt.
        if (customModel?.MetaJson is not null)
        {
            try
            {
                using var meta = JsonDocument.Parse(customModel.MetaJson);
                if (meta.RootElement.TryGetProperty("skill_ids", out var skillIds)
                    && skillIds.ValueKind == JsonValueKind.Array)
                {
                    var ids = skillIds.EnumerateArray()
                        .Select(e => e.GetString()).Where(s => s is not null).ToList();
                    var contents = await db.Skills.AsNoTracking()
                        .Where(s => ids.Contains(s.Id) && s.IsActive)
                        .Select(s => s.Content).ToListAsync(ct);
                    systemParts.AddRange(contents.Where(c => !string.IsNullOrWhiteSpace(c)));
                }
            }
            catch (JsonException)
            {
            }
        }

        // 2. Contexto de arquivos/referências: anexos + #arquivo/#coleção.
        //    Com embeddings disponíveis injeta os top-K chunks por similaridade;
        //    sem provider cai no fallback de texto integral atual.
        var lastUserText = messages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
        var referenced = await ResolveReferenceFileIdsAsync(lastUserText, user.Id, db, ct);
        var scopedFileIds = (request.FileIds ?? [])
            .Concat(referenced).Distinct().ToList();
        if (scopedFileIds.Count > 0)
        {
            var fileContext = lastUserText.Length > 0
                ? await rag.RetrieveAsync(user.Id, lastUserText, scopedFileIds, ct)
                : null;
            fileContext ??= await FileEndpoints.BuildFileContextAsync(
                scopedFileIds, user.Id, db, ct);
            if (!string.IsNullOrEmpty(fileContext))
            {
                systemParts.Add(fileContext);
            }
        }

        // 2.5. Busca web opcional: injeta os resultados como contexto com fontes.
        if (request.WebSearch == true && lastUserText.Length > 0)
        {
            try
            {
                var results = await webSearch.SearchAsync(lastUserText, count: 5, ct);
                if (results is { Count: > 0 })
                {
                    var lines = results.Select((r, i) =>
                        $"[{i + 1}] {r.Title}\nURL: {r.Url}\n{r.Snippet}");
                    systemParts.Add(
                        "Resultados da busca web (use-os para responder e cite as fontes como [n]):\n"
                        + string.Join("\n\n", lines));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Engine indisponível: segue sem contexto web.
            }
        }

        // 3. Memórias persistentes do usuário.
        var adminConfig = await config.GetAdminConfigAsync(ct);
        if (adminConfig.EnableMemories)
        {
            var memories = await db.Memories.AsNoTracking()
                .Where(m => m.UserId == user.Id)
                .Select(m => m.Content)
                .ToListAsync(ct);
            if (memories.Count > 0)
            {
                systemParts.Add(
                    "Memórias do usuário:\n" + string.Join("\n", memories.Select(m => $"- {m}")));
            }
        }

        // 4. Mescla partes de sistema numa única mensagem inicial.
        if (systemParts.Count > 0)
        {
            var merged = string.Join("\n\n", systemParts);
            var existing = messages.FindIndex(m => m.Role == "system");
            if (existing >= 0)
            {
                messages[existing] = messages[existing] with { Content = $"{merged}\n\n{messages[existing].Content}" };
            }
            else
            {
                messages.Insert(0, new ChatCompletionMessage("system", merged));
            }
        }

        var built = request with
        {
            Model = model,
            Messages = messages,
            Params = parameters,
            FileIds = null,
        };

        // 5. Filtros declarativos do modelo custom (inlet) em ordem estável.
        var inletFilters = ModelFilterService.Parse(customModel?.MetaJson);
        return inletFilters.Count > 0
            ? ModelFilterService.ApplyInlet(built, inletFilters)
            : built;
    }

    /// <summary>Resolve referências #nome (arquivo ou coleção) para ids de arquivo.</summary>
    private static async Task<List<string>> ResolveReferenceFileIdsAsync(
        string text, string userId, AppDbContext db, CancellationToken ct)
    {
        var fileIds = new List<string>();
        if (!text.Contains('#'))
        {
            return fileIds;
        }

        var tokens = System.Text.RegularExpressions.Regex
            .Matches(text, @"#([\w.\-]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        if (tokens.Count == 0)
        {
            return fileIds;
        }

        // #coleção → todos os arquivos vinculados.
        var collections = await db.KnowledgeCollections.AsNoTracking()
            .Where(k => k.UserId == userId && tokens.Contains(k.Name))
            .Select(k => k.Id)
            .ToListAsync(ct);
        if (collections.Count > 0)
        {
            fileIds.AddRange(await db.KnowledgeFiles.AsNoTracking()
                .Where(f => collections.Contains(f.CollectionId))
                .Select(f => f.FileId)
                .ToListAsync(ct));
        }

        // #arquivo → arquivo do usuário com esse nome.
        fileIds.AddRange(await db.Files.AsNoTracking()
            .Where(f => f.UserId == userId && tokens.Contains(f.Filename))
            .Select(f => f.Id)
            .ToListAsync(ct));

        return fileIds;
    }

    /// <summary>
    /// Resultado do loop de tools: o conteúdo final do modelo e as
    /// mensagens geradas durante o loop (assistant com ToolCallsJson +
    /// respostas role=tool) para persistência na run
    /// (SPEC-20261007-chat-tool-streaming).
    /// </summary>
    public sealed record ToolLoopOutcome(
        string? FinalContent,
        IReadOnlyList<ChatCompletionMessage> ToolMessages);

    /// <summary>
    /// Callbacks do loop de tools para streaming/gate
    /// (SPEC-20261007-chat-tool-streaming): <paramref name="OnPhaseAsync"/>
    /// recebe a fase (generating|running_tool|awaiting_approval) antes de
    /// cada etapa; <paramref name="OnCallAsync"/> antes de executar;
    /// <paramref name="GateAsync"/> decide se a tool mutável executa
    /// (negado injeta "negado pelo usuário", com a instrução opcional do
    /// dono — SPEC-20261007-chat-agent-ux RF-002); <paramref name="OnResultAsync"/>
    /// recebe (call, output, result, denied) após cada execução/decisão —
    /// <c>result</c> é o payload estruturado opcional (ex.: imagePath de
    /// generate_image).
    /// </summary>
    public sealed record ToolLoopCallbacks(
        Func<string, string?, CancellationToken, Task>? OnPhaseAsync = null,
        Func<ProviderToolCall, CancellationToken, Task>? OnCallAsync = null,
        Func<ProviderToolCall, CancellationToken, Task<ToolGateDecision>>? GateAsync = null,
        Func<ProviderToolCall, string, JsonElement?, bool, CancellationToken, Task>? OnResultAsync = null);

    /// <summary>
    /// Loop de tool calling: chama o modelo com tools até resposta final
    /// (sem tool_calls) ou teto de 5 iterações. <paramref name="callbacks"/>
    /// recebe os eventos de fase/call/gate/resultado (runs desacopladas
    /// emitem no SSE; o endpoint legado passa null — tools invisíveis lá).
    /// </summary>
    public static async Task<ToolLoopOutcome?> RunToolLoopAsync(
        ChatCompletionRequest effective,
        IReadOnlyList<Tool> tools,
        ToolExecutor toolExecutor,
        ProviderService providers,
        CancellationToken ct,
        ToolLoopCallbacks? callbacks = null,
        BuiltinToolContext? builtinContext = null)
    {
        const int maxRounds = 5;
        var messages = effective.Messages.ToList();
        var toolMessages = new List<ChatCompletionMessage>();

        for (var round = 0; round < maxRounds; round++)
        {
            if (callbacks?.OnPhaseAsync is not null)
            {
                await callbacks.OnPhaseAsync("generating", null, ct);
            }
            var step = await providers.CompleteWithToolsAsync(
                effective with { Messages = messages }, ct);
            if (step.ToolCalls.Count == 0)
            {
                return new ToolLoopOutcome(step.Content, toolMessages);
            }

            var assistantMessage = new ChatCompletionMessage(
                "assistant", step.Content, ToolCallsJson: step.ToolCallsJson);
            messages.Add(assistantMessage);
            toolMessages.Add(assistantMessage);

            foreach (var call in step.ToolCalls)
            {
                if (callbacks?.OnPhaseAsync is not null)
                {
                    await callbacks.OnPhaseAsync("running_tool", call.Name, ct);
                }
                if (callbacks?.OnCallAsync is not null)
                {
                    await callbacks.OnCallAsync(call, ct);
                }

                var gate = callbacks?.GateAsync is not null
                    ? await callbacks.GateAsync(call, ct)
                    : ToolGateDecision.Allow;
                var denied = !gate.Approved;
                var outcome = denied
                    ? new ToolExecutionOutcome(
                        string.IsNullOrWhiteSpace(gate.DenyMessage)
                            ? "Erro: execução negada pelo usuário."
                            : $"Erro: execução negada pelo usuário: {gate.DenyMessage}")
                    // ask_user e gates parecidos podem devolver o resultado
                    // direto via ToolGateDecision.Output (RF-005).
                    : gate.Output is { Length: > 0 } gateOutput
                        ? new ToolExecutionOutcome(gateOutput)
                        : await toolExecutor.ExecuteAsync(
                            tools, call.Name, call.ArgumentsJson, builtinContext, ct);
                var output = outcome.Text;

                if (callbacks?.OnResultAsync is not null)
                {
                    await callbacks.OnResultAsync(call, output, outcome.Result, denied, ct);
                }

                var toolMessage = new ChatCompletionMessage(
                    "tool", output, ToolCallId: call.Id);
                messages.Add(toolMessage);
                toolMessages.Add(toolMessage);
            }
        }

        // Teto de iterações atingido: resposta final sem tools.
        var final = await providers.CompleteAsync(
            effective with { Messages = messages, Tools = null }, ct);
        return new ToolLoopOutcome(final, toolMessages);
    }

    /// <summary>
    /// Executa uma batalha de arena quando o modelo pedido é do tipo arena:
    /// sorteia 2 concorrentes do MetaJson, completa ambos e emite um payload
    /// {"arena": {battle_id, responses:[{label, content}]}} via
    /// <paramref name="emitLineAsync"/> (linha SSE `data: ...` pronta).
    /// Retorna true quando tratou a requisição (mesmo em erro já emitido).
    /// </summary>
    public static async Task<bool> TryRunArenaAsync(
        ChatCompletionRequest request,
        string modelKey,
        User user,
        AppDbContext db,
        ConfigService config,
        RagService rag,
        ProviderService providers,
        WebSearchService webSearch,
        Func<string, Task> emitLineAsync,
        CancellationToken ct)
    {
        var arenaEntry = await db.ModelEntries.AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.IsActive && (m.Id == modelKey || m.Name == modelKey)
                    && (m.UserId == user.Id || m.UserId == "public"), ct);
        var arena = arenaEntry is null ? null : ModelEndpoints.ParseArenaMeta(arenaEntry.MetaJson);
        if (arena is null)
        {
            return request.Model.StartsWith("arena:", StringComparison.Ordinal);
        }
        if (!await ModelEndpoints.HasModelAccessAsync(user, arenaEntry!, db, ct))
        {
            await EmitDoneErrorAsync(emitLineAsync, "Acesso negado ao modelo arena.");
            return true;
        }

        var competitors = arena.Value.ModelIds
            .OrderBy(_ => Random.Shared.Next())
            .Take(2)
            .ToList();
        if (competitors.Count < 2)
        {
            await EmitDoneErrorAsync(emitLineAsync, "Modelo arena sem concorrentes suficientes.");
            return true;
        }

        var responses = new List<string>(2);
        try
        {
            foreach (var competitor in competitors)
            {
                var effective = await EnrichRequestAsync(
                    request with { Model = competitor, Stream = false },
                    user, db, config, rag, webSearch, ct);
                responses.Add(await providers.CompleteAsync(effective, ct));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            await EmitDoneErrorAsync(emitLineAsync, $"Falha ao gerar respostas da arena: {ex.Message}");
            return true;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var battle = new ArenaBattle
        {
            UserId = user.Id,
            ArenaModelId = arenaEntry!.Id,
            ModelA = competitors[0],
            ModelB = competitors[1],
            ResponseA = responses[0],
            ResponseB = responses[1],
            CreatedAt = now,
        };
        db.ArenaBattles.Add(battle);
        await db.SaveChangesAsync(ct);

        var payload = JsonSerializer.Serialize(new
        {
            arena = new
            {
                battle_id = battle.Id,
                responses = new[]
                {
                    new { label = "A", content = responses[0] },
                    new { label = "B", content = responses[1] },
                },
            },
        });
        await emitLineAsync($"data: {payload}");
        await emitLineAsync("data: [DONE]");
        return true;
    }

    /// <summary>
    /// Prepara o roteamento `pipeline:{id}`: resolve o servidor que hospeda o
    /// pipe e monta o corpo (modelo/messages/stream) + valves das functions
    /// ativas. Retorna null quando o pipe é desconhecido.
    /// </summary>
    public static async Task<(PipelineServer? Server, string Body, string? ValvesJson)>
        PreparePipelineRouteAsync(
            ChatCompletionRequest request,
            AppDbContext db,
            PipelineClientService pipelines,
            CancellationToken ct)
    {
        var pipeId = request.Model["pipeline:".Length..];
        var server = await pipelines.FindServerForPipeAsync(pipeId, ct);
        if (server is null)
        {
            return (null, string.Empty, null);
        }

        // Valves de functions ativas — enviadas ao servidor como {"valves": {id: {...}}}.
        var functions = await db.Functions.AsNoTracking()
            .Where(f => f.Active && f.ValvesJson != null)
            .Select(f => new { f.Id, f.ValvesJson }).ToListAsync(ct);
        string? valvesJson = null;
        if (functions.Count > 0)
        {
            var map = new Dictionary<string, JsonElement>();
            foreach (var f in functions)
            {
                try
                {
                    map[f.Id] = JsonSerializer.Deserialize<JsonElement>(f.ValvesJson!);
                }
                catch (JsonException)
                {
                }
            }

            valvesJson = JsonSerializer.Serialize(map);
        }

        var body = JsonSerializer.Serialize(new
        {
            model = pipeId,
            messages = request.Messages,
            stream = request.Stream,
        });
        return (server, body, valvesJson);
    }

    /// <summary>Emite uma linha de erro + [DONE] no formato SSE atual.</summary>
    public static async Task EmitDoneErrorAsync(Func<string, Task> emitLineAsync, string message)
    {
        var error = JsonSerializer.Serialize(new { error = message });
        await emitLineAsync($"data: {error}");
        await emitLineAsync("data: [DONE]");
    }
}

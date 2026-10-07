using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Completions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
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
    ILogger<ChatRunExecutor> logger)
{
    /// <summary>Bytes de delta acumulados antes de gravar um checkpoint.</summary>
    private const int CheckpointBytes = 2048;

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

        try
        {
            // Modelos pipeline:{id} → roteia ao servidor de pipelines e repassa as linhas.
            if (request.Model.StartsWith("pipeline:", StringComparison.Ordinal))
            {
                await RunPipelineAsync(request, run, ct);
                await FinishAsync(run, string.Empty, ct);
                return;
            }

            // Arena gera as duas respostas e publica o payload {"arena":...}.
            var arenaModel = request.Model.StartsWith("arena:", StringComparison.Ordinal)
                ? request.Model["arena:".Length..]
                : request.Model;
            if (await ChatPipeline.TryRunArenaAsync(
                request, arenaModel, user, db, config, rag, providers, webSearch,
                line => { broadcaster.Publish(run.Id, line); return Task.CompletedTask; },
                ct))
            {
                await FinishAsync(run, string.Empty, ct);
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
                var finished = await ChatPipeline.RunToolLoopAsync(
                    effective, tools, toolExecutor, providers, ct);
                if (finished is not null)
                {
                    foreach (var (regex, replacement) in outletRules)
                    {
                        finished = regex.Replace(finished, replacement);
                    }
                    var chunk = JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { index = 0, delta = new { content = finished } } },
                    });
                    broadcaster.Publish(run.Id, $"data: {chunk}");
                    content.Append(finished);
                }
            }
            else
            {
                await foreach (var line in providers.StreamCompletionAsync(effective, ct))
                {
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

            broadcaster.Publish(run.Id, "data: [DONE]");
            await FinishAsync(run, content.ToString(), ct);
        }
        catch (OperationCanceledException)
        {
            broadcaster.Publish(run.Id, "data: [DONE]");
            await FinishAsync(run, content.ToString(), ct, ChatRunStatus.Stopped);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            var message = ex is HttpRequestException or TaskCanceledException
                ? $"Falha ao contactar o provedor: {ex.Message}"
                : ex.Message;
            broadcaster.Publish(run.Id, $"data: {JsonSerializer.Serialize(new { error = message })}");
            broadcaster.Publish(run.Id, "data: [DONE]");
            run.Error = message;
            await FinishAsync(run, content.ToString(), ct, ChatRunStatus.Failed);
        }
    }

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
            broadcaster.Publish(run.Id, "data: [DONE]");
            return;
        }

        var proxied = await pipelines.RouteCompletionAsync(server, body, valvesJson, ct);
        if (proxied.Response is null)
        {
            broadcaster.Publish(run.Id, $"data: {JsonSerializer.Serialize(new { error = $"Falha no servidor de pipelines: {proxied.Error}" })}");
            broadcaster.Publish(run.Id, "data: [DONE]");
            return;
        }

        using var upstream = proxied.Response;
        if (!upstream.IsSuccessStatusCode)
        {
            var body_ = await upstream.Content.ReadAsStringAsync(ct);
            broadcaster.Publish(run.Id, $"data: {JsonSerializer.Serialize(new { error = $"Pipeline devolveu {(int)upstream.StatusCode}: {Truncate(body_, 500)}" })}");
            broadcaster.Publish(run.Id, "data: [DONE]");
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

        broadcaster.Publish(run.Id, "data: [DONE]");
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

    /// <summary>Grava a mensagem do assistant no chat e finaliza a run.</summary>
    private async Task FinishAsync(
        ChatRun run, string content, CancellationToken ct, string? status = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        run.Status = status ?? ChatRunStatus.Completed;
        run.CompletedAt = now;
        run.PartialContent = content;

        if (content.Length > 0)
        {
            var chat = await db.Chats.Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == run.ChatId, CancellationToken.None);
            if (chat is not null)
            {
                var nextPosition = chat.Messages.Count == 0
                    ? 0
                    : chat.Messages.Max(m => m.Position) + 1;
                chat.Messages.Add(new ChatMessage
                {
                    ChatId = chat.Id,
                    Role = "assistant",
                    Content = content,
                    Model = run.Model,
                    Position = nextPosition,
                    Timestamp = now,
                });
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

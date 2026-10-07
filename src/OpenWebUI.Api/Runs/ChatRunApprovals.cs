using System.Collections.Concurrent;

namespace OpenWebUI.Api.Runs;

/// <summary>
/// Gate de aprovação de tools (SPEC-20261007-chat-tool-streaming RF-003):
/// a run pausa num <see cref="TaskCompletionSource{TResult}"/> por
/// <c>runId:callId</c> até o dono decidir via endpoint
/// (<see cref="ResolveAsync"/>), o timeout expirar (deny) ou a run ser
/// interrompida (<see cref="Cancel"/>). <c>remember</c> vale só para a
/// conversa e só em memória — um restart devolve a run a interrupted de
/// qualquer forma.
/// </summary>
public sealed class ChatRunApprovals
{
    /// <summary>Tempo máximo esperando uma decisão antes de negar.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>Resultado da espera: aprovado, ou negado com mensagem.</summary>
    public sealed record ApprovalResult(bool Approved, string? Message);

    private sealed record Pending(
        string ChatId,
        string ToolName,
        TaskCompletionSource<ApprovalResult> Completion);

    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _remembered =
        new(StringComparer.Ordinal);

    private static string Key(string runId, string callId) => $"{runId}:{callId}";

    /// <summary>Se o dono já aprovou <paramref name="toolName"/> com remember neste chat.</summary>
    public bool IsRemembered(string chatId, string toolName) =>
        _remembered.TryGetValue(chatId, out var tools) && tools.ContainsKey(toolName);

    /// <summary>Se há uma aprovação pendente nesta run/call.</summary>
    public bool IsPending(string runId, string callId) =>
        _pending.ContainsKey(Key(runId, callId));

    /// <summary>
    /// Registra a pendência e espera a decisão. Resolve aprovado, ou negado
    /// (decisão — com a mensagem de instrução opcional —, timeout ou
    /// cancelamento da run).
    /// </summary>
    public async Task<ApprovalResult> WaitAsync(
        string runId, string chatId, string callId, string toolName,
        CancellationToken ct)
    {
        var pending = new Pending(chatId, toolName, new TaskCompletionSource<ApprovalResult>(
            TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_pending.TryAdd(Key(runId, callId), pending))
        {
            return new ApprovalResult(false, null);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            await Task.WhenAny(pending.Completion.Task,
                Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, timeout.Token));
            return pending.Completion.Task is { IsCompletedSuccessfully: true } task
                ? task.Result
                : new ApprovalResult(false, null);
        }
        catch (OperationCanceledException)
        {
            return new ApprovalResult(false, null);
        }
        finally
        {
            _pending.TryRemove(Key(runId, callId), out _);
        }
    }

    /// <summary>
    /// Aplica a decisão do dono. Retorna false quando a call não está
    /// pendente (já decidida, expirada ou inexistente → endpoint 404).
    /// </summary>
    public bool Resolve(
        string runId, string chatId, string callId,
        bool approved, bool remember, string? message = null)
    {
        if (!_pending.TryGetValue(Key(runId, callId), out var pending)
            || pending.ChatId != chatId)
        {
            return false;
        }

        if (remember && approved)
        {
            _remembered.GetOrAdd(chatId, _ => new ConcurrentDictionary<string, bool>())
                [pending.ToolName] = true;
        }

        return pending.Completion.TrySetResult(new ApprovalResult(approved, message));
    }

    /// <summary>Nega todas as aprovações pendentes da run (stop/shutdown).</summary>
    public void Cancel(string runId)
    {
        foreach (var (key, pending) in _pending)
        {
            if (key.StartsWith($"{runId}:", StringComparison.Ordinal))
            {
                pending.Completion.TrySetResult(new ApprovalResult(false, null));
            }
        }
    }
}

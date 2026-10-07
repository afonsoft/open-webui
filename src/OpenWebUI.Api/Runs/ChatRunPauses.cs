using System.Collections.Concurrent;

namespace OpenWebUI.Api.Runs;

/// <summary>
/// Gates de pausa por run (SPEC-20261007-chat-agent-parity RF-013): cada
/// run tem um <see cref="SemaphoreSlim"/> de 1 slot — pausar drena o slot e
/// os checkpoints do executor bloqueiam até o resume liberar. Em memória:
/// uma pausa não sobrevive a restart (o sweep marca paused → interrupted).
/// </summary>
public sealed class ChatRunPauses
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    private SemaphoreSlim GateFor(string runId) =>
        _gates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));

    /// <summary>Marca pausa: o próximo checkpoint da run bloqueia.</summary>
    public void Pause(string runId) => GateFor(runId).Wait(0);

    /// <summary>Libera quem estiver esperando e os próximos checkpoints.</summary>
    public void Resume(string runId)
    {
        var gate = GateFor(runId);
        if (gate.CurrentCount == 0)
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Checkpoint do executor: retorna na hora fora de pausa; enquanto a
    /// run estiver pausada, bloqueia até resume (ou cancelamento por stop).
    /// </summary>
    public async Task WaitIfPausedAsync(string runId, CancellationToken ct)
    {
        var gate = GateFor(runId);
        await gate.WaitAsync(ct);
        gate.Release();
    }

    /// <summary>Esquece o gate ao fim da run.</summary>
    public void Forget(string runId) => _gates.TryRemove(runId, out _);
}

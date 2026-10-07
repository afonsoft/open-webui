using System.Text.Json;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:todo_write</c> — atualiza a lista de tarefas da run
/// (SPEC-20261007-chat-agent-parity RF-010): o plano de trabalho do
/// agente vira um snapshot emitido como evento SSE <c>tasks</c> (o
/// executor republica o array <c>tasks</c> do payload estruturado) e fica
/// visível no card da tool/painel. Estado interno da run — sem aprovação.
/// </summary>
public sealed class TodoWriteBuiltinTool : IBuiltinChatTool
{
    /// <summary>Cap de itens por lista.</summary>
    private const int MaxItems = 50;

    /// <summary>Cap de caracteres por item.</summary>
    private const int MaxContentChars = 300;

    /// <inheritdoc />
    public string Name => "todo_write";

    /// <inheritdoc />
    public string Description =>
        "Atualiza a lista de tarefas do plano da run (visível ao usuário). "
        + "Chame ao decompor o trabalho e depois a cada conclusão — "
        + "status: pending | in_progress | completed. Sempre envie a "
        + "lista inteira (é um snapshot, não um delta).";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "todos": {
              "type": "array",
              "description": "Snapshot completo da lista, em ordem.",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string", "description": "Id estável (auto 't1', 't2', ... se omitido)." },
                  "content": { "type": "string", "description": "Descrição da tarefa." },
                  "status": {
                    "type": "string",
                    "enum": ["pending", "in_progress", "completed"],
                    "description": "Estado atual."
                  }
                },
                "required": ["content", "status"]
              }
            }
          },
          "required": ["todos"]
        }
        """;

    /// <inheritdoc />
    public bool RequiresApproval => false;

    /// <inheritdoc />
    public Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        if (!args.TryGetProperty("todos", out var todosEl)
            || todosEl.ValueKind != JsonValueKind.Array)
        {
            return Task.FromResult(new BuiltinToolResult(
                "Parâmetro 'todos' (array) é obrigatório."));
        }

        var items = new List<object>();
        var done = 0;
        var doing = 0;
        var i = 0;
        foreach (var el in todosEl.EnumerateArray())
        {
            if (items.Count >= MaxItems)
            {
                break;
            }

            if (el.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var content = el.TryGetProperty("content", out var c) ? c.GetString() : null;
            var status = el.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(content)
                || status is not ("pending" or "in_progress" or "completed"))
            {
                return Task.FromResult(new BuiltinToolResult(
                    "Todo item exige 'content' e 'status' ∈ pending|in_progress|completed."));
            }

            var id = el.TryGetProperty("id", out var idEl) && !string.IsNullOrWhiteSpace(idEl.GetString())
                ? idEl.GetString()!
                : $"t{++i}";
            if (content.Length > MaxContentChars)
            {
                content = content[..MaxContentChars] + "…";
            }

            if (status == "completed")
            {
                done++;
            }
            else if (status == "in_progress")
            {
                doing++;
            }

            items.Add(new { id, content, status });
        }

        var text = $"Plano atualizado: {items.Count} tarefa(s) — "
            + $"{done} concluída(s), {doing} em andamento.";
        return Task.FromResult(new BuiltinToolResult(text, new { tasks = items }));
    }
}

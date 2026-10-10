# perf/ — load-test harness

Ferramentas para teste de carga da API do Open WebUI (.NET): mock Ollama rápido +
cliente async que mede throughput, latências (p50/p90/p95/p99) e erros.

## Componentes

- `mock_ollama.py` — servidor Ollama falso em `127.0.0.1:11434` (aiohttp).
  Responde `/api/tags`, `/api/chat` (NDJSON stream ou JSON único), `/api/embed`,
  `/api/embeddings`, `/api/show`, `/api/version`. Gatilhos no conteúdo da
  última mensagem do usuário:
  - `__delegate__` / `__delegate_N__` → `tool_calls` de `builtin:delegate_task`
    com `wait=false` (fan-out de sub-agentes);
  - `__words__N` → N pedaços de conteúdo no stream;
  - `__sleep__MS` → latência artificial antes de responder.
- `loadtest.py` — harness async (aiohttp) com os cenários:
  - `rps` — mix ponderado de endpoints quentes (create/list/get chat, enqueue,
    runs, models) por `--duration` segundos com `--workers` workers;
  - `sessions` — `--users` usuários × `--chats-per-user` chats × `--rounds`
    rounds, cada chat mantendo uma run ativa (enqueue → SSE attach → fim);
  - `subagents` — `--fanout` chats disparam `delegate_task wait=false`
    (`--delegates-per-chat` por turno, `--waves` ondas); auto-aprova o gate de
    tools via SSE `approval_asked` e conta filhos spawnados/completados;
  - `real` — mesmo formato de `sessions` com `--real-model` (provider real).

## Como rodar

```bash
pip install aiohttp

# 1. mock provider
python3 tools/perf/mock_ollama.py &

# 2. app com mock + provider real (opcional)
OLLAMA_BASE_URL=http://127.0.0.1:11434 \
OPENAI_API_BASE_URL=$LLM_BASE_URL OPENAI_API_KEY=$LLM_API_KEY \
"ConnectionStrings__Default=Data Source=/tmp/loadtest.db" \
dotnet run --project src/OpenWebUI.Api -c Release --urls http://localhost:8080 &

# 3. passe mock (≥30 req/s + sessões + sub-agentes)
python3 tools/perf/loadtest.py \
  --base http://localhost:8080 \
  --email perf@load.test --password loadtest123 \
  --model mock:latest \
  --scenario rps,sessions,subagents \
  --duration 60 --workers 24 \
  --json /tmp/metrics-mock.json --md /tmp/report-mock.md

# 4. passe real (escala menor)
python3 tools/perf/loadtest.py \
  --base http://localhost:8080 \
  --email perf@load.test --password loadtest123 \
  --real-model "$LLM_MODEL" --scenario real \
  --real-users 2 --real-chats-per-user 2 --real-rounds 1
```

Notas:
- O primeiro signup vira admin; os demais ficam `pending` — o harness aprova
  automaticamente via `POST /api/v1/users/{id}/update/role` como admin.
- `GET /api/v1/chats/runs` retorna no máx. 50 runs — contadores de fila
  (`queue_max_pending`) saturam em 50.
- O dispatcher serializa runs por chat e limita a 4 execuções globais
  (`ChatRunDispatcher.MaxConcurrent`); latências de cauda em `run_e2e`
  refletem espera na fila, não falha.

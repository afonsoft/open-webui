# Integrações e Extensões

## Providers de IA

| Provider | Configuração |
|---|---|
| Ollama | `OLLAMA_BASE_URL` / `OLLAMA_BASE_URLS` (`;`-separados) |
| OpenAI-compatível | `OPENAI_API_BASE_URL(S)`, `OPENAI_API_KEY(S)` |

Seed de conexões acontece apenas no primeiro boot (env → tabela de conexões). Depois, gerenciadas em Configurações → Conexões.

## Harness de agentes

| Componente | Origem |
|---|---|
| `.devin/skills/` | catálogo `afonsoft/skills` (spec-driven: orchestrator, write-specs, execute-specs, qa-analyst, gap-analysis, code-review-and-quality, create-issues, architecture, design, diagnose, ...) |
| `.devin/blueprint.yaml` | setup de ambiente Devin (git-managed) |
| `.claude/` | harness local (rules, sub-agents, skills do repo, knowledge, memory) |

## Docker

`Dockerfile` multi-stage (SDK → publish → runtime ASP.NET) e `docker-compose.yaml` com volume para `webui.db` e portas `3000:8080`.

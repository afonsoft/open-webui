# TOOLS.md — Ferramentas e Integrações

## Build & Test

| Ferramenta | Comando |
|---|---|
| SDK .NET | `/home/ubuntu/.dotnet/dotnet` (10.0.401) |
| Restore | `dotnet restore OpenWebUI.slnx` |
| Build | `dotnet build OpenWebUI.slnx --configuration Release` |
| Testes | `dotnet test tests/OpenWebUI.Api.Tests` |
| Run | `dotnet run --project src/OpenWebUI.Api` (:8080) |

## Frontend

| Item | Detalhe |
|---|---|
| Tailwind CLI | `tailwindcss -i src/OpenWebUI.Client/tailwind.input.css -o src/OpenWebUI.Client/wwwroot/css/tailwind.css --minify` |
| Tema | `.dark` no `<html>`; `localStorage webui.theme` (`dark`/`light`/`system`) |
| Upstream ref | `git fetch upstream --depth 1 && git show FETCH_HEAD:src/lib/components/...` |

## Docker

```bash
docker build -t openwebui-dotnet:local .
docker compose up -d   # http://localhost:3000
```

## Integrações externas

| Serviço | Uso | Config |
|---|---|---|
| Ollama | provider de modelos/chat | `OLLAMA_BASE_URL(S)` (env, `;`-sep, primeiro boot) |
| OpenAI-compatible | provider alternativo | `OPENAI_API_BASE_URL(S)`, `OPENAI_API_KEY(S)` |
| GitHub | issues/PRs/CI | `gh` CLI + Devin git tools |

## Convenções de dados

- SQLite `webui.db` (não commitar); schema evoluído por `SchemaBootstrap`.
- Auth: JWT ou `Bearer sk-*`; primeiro usuário = admin.

# Open WebUI — .NET

Migração do [Open WebUI](https://github.com/open-webui/open-webui) para
**.NET 10 · C# 14 · Blazor WebAssembly** (SvelteKit + FastAPI → ASP.NET Core + Blazor),
mantendo o layout, as funcionalidades e a configuração do original.

[![CI Build & Test](https://github.com/afonsoft/open-webui/actions/workflows/ci-build-test.yml/badge.svg)](https://github.com/afonsoft/open-webui/actions/workflows/ci-build-test.yml)
[![Code Quality](https://github.com/afonsoft/open-webui/actions/workflows/code-quality.yml/badge.svg)](https://github.com/afonsoft/open-webui/actions/workflows/code-quality.yml)
[![Security Scan](https://github.com/afonsoft/open-webui/actions/workflows/security-scan.yml/badge.svg)](https://github.com/afonsoft/open-webui/actions/workflows/security-scan.yml)
[![Accessibility Audit](https://github.com/afonsoft/open-webui/actions/workflows/a11y-audit.yml/badge.svg)](https://github.com/afonsoft/open-webui/actions/workflows/a11y-audit.yml)

## Screenshots

| Chat (escuro) | Chat (claro) | Login |
|---|---|---|
| ![Chat — tema escuro](docs/screenshots/chat-dark.png) | ![Chat — tema claro](docs/screenshots/chat-light.png) | ![Login](docs/screenshots/auth-dark.png) |

| Admin | Workspace | Mobile |
|---|---|---|
| ![Administração](docs/screenshots/admin-dark.png) | ![Workspace](docs/screenshots/workspace-dark.png) | ![Chat mobile](docs/screenshots/chat-mobile.png) |

| Tools no chat | Terminal embutido | MCPs no Admin |
|---|---|---|
| ![Tool calls e aprovação](docs/screenshots/chat-tools.png) | ![Terminal PTY](docs/screenshots/terminal.png) | ![Servidores MCP](docs/screenshots/mcp-settings.png) |

## Funcionalidades

- **Chat** com streaming SSE (Ollama / OpenAI-compat), anexos, avaliação 👍/👎,
  edição, regeneração, título/follow-ups/tags gerados por LLM
- **Runs desacopladas** — a resposta roda no servidor: fechar/recarregar a aba não
  interrompe; ao reabrir, o cliente anexa na run ativa com replay do stream
- **Notificações** — toast in-app, Notification API e Web Push (VAPID) para avisar
  quando a resposta termina com o site fechado
- **Tool streaming + aprovação** — `tool_call`/`tool_result`/status no SSE, gate de
  aprovação para tools mutáveis com preset por conversa (readonly/aprovar/sempre)
- **Builtin agent tools** — `generate_image` (imagem inline), `code_interpreter`,
  `shell_exec` + jobs em background, `fetch_url`, `web_search`
- **Terminal embutido** — PTY real via WebSocket + xterm.js (`/terminal`, flag admin)
- **Canais em grupo** com realtime (SignalR `/ws`), menção `@modelo`, typing/presence
- **Knowledge/RAG** — coleções com embeddings e retrieval vetorial em SQLite
  (cosseno + BM25 híbrido, chunking por frase, rerank por provider externo)
- **MCP** — servidores Model Context Protocol configurados em Configurações → Admin,
  com teste de conexão e tools expansíveis; tools MCP entram no loop do chat
- **Tools** — tools HTTP com function calling (loop no servidor, URL de execução nunca exposta)
- **Geração de imagens** — provider OpenAI Images, botão por mensagem
- **Execução de código** — blocos do chat: JS em Web Worker e Python via Pyodide WASM
- **Voz** — ditado (STT), leitura de mensagens (TTS) e modo Call via Web Speech API
- **Automações** — execuções agendadas (intervalo/diário/semanal) + calendário mensal
- **Auth** — JWT, chaves `sk-*`, OAuth/OIDC (Google/GitHub/Microsoft), SAML, LDAP, SCIM, grupos/RBAC
- **PWA** — manifest + service worker do shell, instalável e offline-safe
- **i18n** — 8 locales (pt-BR, en-US, es, fr, de, it, ja, zh) com troca sem reload
- **Mobile-first** — sidebar gaveta, navegação por breakpoints, auditoria axe-core no CI
- **Admin** — usuários, grupos, conexões, avaliações, feature flags e analytics

Mapa de paridade detalhado: [`docs/MIGRACAO-DOTNET.md`](docs/MIGRACAO-DOTNET.md).
Arquitetura: [`docs/architecture/architecture.md`](docs/architecture/architecture.md).

## Comparativo com o Open WebUI original

### Stack

| Camada | open-webui (original) | Este fork |
| --- | --- | --- |
| Frontend | SvelteKit + Tailwind | Blazor WebAssembly + Tailwind v4 (mesmo tema/oklch) |
| Backend | Python FastAPI | ASP.NET Core 10 (Minimal APIs) |
| Linguagem | Python 3.11+ | C# 14 (.NET 10) |
| Banco | SQLAlchemy + SQLite/Postgres | EF Core 10 + SQLite (EF Migrations + baseline de `webui.db` legadas) |
| Realtime | socket.io (Python) | SignalR `/ws` |
| Streaming | SSE | SSE server→client |
| Auth | JWT + bcrypt | JWT + PBKDF2 (PasswordHasher ASP.NET Core) |
| Schema | Alembic/Peewee (migrações Python) | `DatabaseMigrator` + EF Migrations versionadas |
| Execução de tools | Python arbitrário no host | Tools HTTP declarativas + loop server-side |
| Distribuição | Docker / pip / uv | Dockerfile multi-stage / `dotnet` / GHCR + Docker Hub |

### Paridade por área

| Área | Original | Este fork |
| --- | --- | --- |
| Chat (CRUD, pastas, tags, share `/s/`, versões de mensagem) | ✅ | ✅ portado |
| Routers de API (33) | ✅ | ~31 portados · 2 parciais (decisão documentada) |
| Rotas de página (47) | ✅ | ~46 — falta só edição inline de functions no admin |
| Canais/DM + presença | socket.io | ✅ SignalR (mesmo comportamento) |
| Knowledge/RAG | ChromaDB/pgvector externo | ✅ SQLite vetorial embutido (cosseno + BM25, L2 persistida) |
| Tools (function calling) | HTTP + código Python | ✅ HTTP declarativas · código arbitrário **não executado** (decisão) |
| MCP tool servers | ✅ | 🟡 SPEC escrita — ver `.specs/SPEC-20261003-mcp-tool-servers.md` |
| Pipelines/functions/skills | ✅ | ✅ registry de functions + pipeline servers externos + skills |
| Arena (battles + ELO) | ✅ | ✅ portado |
| Geração de imagens | ✅ | ✅ OpenAI Images |
| Execução de código (Pyodide) | ✅ | ✅ JS Worker + Python WASM |
| Automações + calendário | ✅ | ✅ portado |
| Auth OAuth/OIDC + SAML + LDAP + SCIM | ✅ | ✅ portado |
| Multi-instância (Postgres/Redis/S3) | ✅ | ❌ não implementado — SQLite + disco local, single-instance |
| i18n | ~30 locales | ✅ 8 locales (fallback en-US → pt-BR → chave) |
| PWA/offline | ✅ | ✅ manifest + service worker |
| Web Search engines | ✅ | ✅ engines configuráveis (tavily, duckduckgo etc.) |

### Divergências intencionais

- **Sem execução de código do usuário no servidor**: tools Python arbitrárias e
  spawn de Jupyter/PTY local do upstream foram substituídos por tools HTTP
  declarativas e proxy de terminal externo (documentado em MIGRACAO-DOTNET).
- **RAG self-contained**: embeddings e busca vetorial em SQLite, sem ChromaDB/
  pgvector externo; rerank via provider quando configurado.
- **Single-instance por padrão**: SQLite + uploads em `data/uploads` (fora do
  wwwroot). Postgres/Redis/S3 são roadmap, não código.
- **Qualidade no CI**: coverage gate com baseline ratchet, auditoria de
  acessibilidade axe-core (mobile+desktop) e freshness de Tailwind por build.

## Estrutura (Clean Architecture)

```
├── OpenWebUI.slnx
├── src/
│   ├── OpenWebUI.Domain/          # Entidades de domínio (sem dependências)
│   ├── OpenWebUI.Application/     # Contratos/DTOs compartilhados
│   ├── OpenWebUI.Infrastructure/  # EF Core (AppDbContext + Migrations), serviços
│   │                              # (JWT, providers, RAG, imagens, automations)
│   ├── OpenWebUI.Api/             # Minimal APIs + SignalR + hosting do Blazor WASM
│   └── OpenWebUI.Client/          # SPA Blazor WebAssembly (UI do chat)
└── tests/
    ├── OpenWebUI.Api.Tests/       # Testes de integração NUnit (518)
    └── OpenWebUI.Client.Tests/    # Testes NUnit dos serviços do cliente (12)
```

Dependências apontam para dentro: `Api → Infrastructure → Application → Domain`.
O `Client` consome apenas `Application` (contratos).

## Executar

Requisito: SDK do .NET 10.

```bash
dotnet restore OpenWebUI.slnx
dotnet run --project src/OpenWebUI.Api
# http://localhost:8080 — o primeiro usuário cadastrado vira admin
```

Docker:

```bash
cp .env.exemplo .env      # ajuste as variáveis (opcional)
docker compose up -d --build
# http://localhost:3032 (WEBUI_PORT no .env)
# admin inicial semeado via ADMIN_NAME/ADMIN_EMAIL/ADMIN_PASSWORD no .env
```

`docker-compose.yaml` sobe só o app — providers (Ollama/OpenAI) vêm do `.env`
ou de Configurações → Conexões. Para testes locais com toda a infra
(Ollama + Whisper já semeados):

```bash
docker compose -f docker-compose.full.yaml up -d --build
docker exec -it ollama ollama pull llama3.2   # baixar um modelo
```

Imagem publicada (tag `vX.Y.Z` gera release no GHCR + Docker Hub):

```bash
docker run -p 3032:8080 ghcr.io/afonsoft/open-webui:latest
# ou docker.io/afonsoft/open-webui:latest
```

Guia completo de deploy Docker: [docs/pt/DEPLOY-DOCKER.md](docs/pt/DEPLOY-DOCKER.md) / [docs/en/DEPLOY-DOCKER.md](docs/en/DEPLOY-DOCKER.md).

## Testes

```bash
dotnet test OpenWebUI.slnx
# 518 testes de API + 12 de cliente (NUnit)

# Cobertura (Coverlet, exclui Client WASM, código gerado e assemblies de teste)
dotnet test tests/OpenWebUI.Api.Tests \
  --collect:"XPlat Code Coverage" --settings coverlet.runsettings
```

Cobertura de linha/branch por projeto (Coverlet 10):

| Camada | Linhas | Branches |
|--------|--------|----------|
| Domain | 100% | 100% |
| Application | 99,0% | 81,8% |
| Infrastructure | 84,6% | 64,8% |
| Api | 87,9% | 71,3% |
| **Total** | **87,4%** | **68,6%** |

O CI impõe um **gate de cobertura**: `.ci/coverage-baseline.txt` é o piso
exigido em todo PR; o job `coverage-ratchet` sobe a baseline automaticamente
quando a cobertura na `main` melhora (ratchet — nunca desce).

## Qualidade e CI

| Workflow | O que cobre |
| --- | --- |
| `ci-build-test.yml` | Build Release, todos os testes + coverage gate, validação do payload WASM, freshness do `tailwind.css`, build da imagem Docker, ratchet do baseline |
| `a11y-audit.yml` | axe-core (Playwright) contra o app publicado — mobile 375px + desktop, falha em violações `critical` |
| `code-quality.yml` | Qodana e SonarQube (opcionais, secret-gated) |
| `security-scan.yml` | CodeQL (C# + JS), Trivy, GitGuardian, Snyk |
| `release.yml` | Tag `vX.Y.Z` → imagem GHCR + Docker Hub (tag = versão) |

## Documentação

- [`docs/MIGRACAO-DOTNET.md`](docs/MIGRACAO-DOTNET.md) — mapa de paridade upstream → .NET
- [`docs/architecture/architecture.md`](docs/architecture/architecture.md) — diagrama Mermaid da arquitetura
- [`docs/specs/`](docs/specs/) — SPEC SDDs entregues; `.specs/` — SPECs em andamento
- [`CHANGELOG.md`](CHANGELOG.md)

## Licença

BSD-3-Clause — ver [`LICENSE`](LICENSE).

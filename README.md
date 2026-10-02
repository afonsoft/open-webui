# Open WebUI — .NET

Migração do [Open WebUI](https://github.com/open-webui/open-webui) para
**.NET 10 · C# 14 · Blazor WebAssembly** (SvelteKit + FastAPI → ASP.NET Core + Blazor),
mantendo o layout, as funcionalidades e a configuração do original.

## Funcionalidades

- **Chat** com streaming SSE (Ollama / OpenAI-compat), anexos, avaliação 👍/👎,
  edição, regeneração, título/follow-ups/tags gerados por LLM
- **Canais em grupo** com realtime (SignalR `/ws`), menção `@modelo`, typing/presence
- **Knowledge/RAG** — coleções com embeddings e retrieval vetorial em SQLite
- **Tools** — tools HTTP com function calling (loop no servidor, URL de execução nunca exposta)
- **Geração de imagens** — provider OpenAI Images, botão por mensagem
- **Execução de código** — blocos do chat: JS em Web Worker e Python via Pyodide WASM
- **Voz** — ditado (STT), leitura de mensagens (TTS) e modo Call via Web Speech API
- **Automações** — execuções agendadas (intervalo/diário/semanal) + calendário mensal
- **Auth** — JWT, chaves `sk-*`, OAuth/OIDC (Google/GitHub/Microsoft), LDAP, grupos/RBAC
- **PWA** — manifest + service worker do shell, instalável e offline-safe
- **i18n** — pt-BR/en-US com troca de idioma sem reload
- **Admin** — usuários, conexões, avaliações, feature flags e dashboard de analytics

Mapa de paridade detalhado: [`docs/MIGRACAO-DOTNET.md`](docs/MIGRACAO-DOTNET.md).
Arquitetura: [`docs/architecture/architecture.md`](docs/architecture/architecture.md).

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
    ├── OpenWebUI.Api.Tests/       # Testes de integração NUnit
    └── OpenWebUI.Client.Tests/    # Testes NUnit dos serviços do cliente
```

Dependências apontam para dentro: `Api → Infrastructure → Application → Domain`.
O `Client` consome apenas `Application` (contratos).

| Camada    | Original                     | Migrado                                    |
| --------- | ---------------------------- | ------------------------------------------ |
| Frontend  | SvelteKit + Tailwind         | Blazor WebAssembly + Tailwind v4           |
| Backend   | Python FastAPI               | ASP.NET Core 10 (Minimal APIs)             |
| Banco     | SQLAlchemy + SQLite/Postgres | EF Core 10 + SQLite (Migrations)           |
| Auth      | JWT + bcrypt                 | JWT + PBKDF2 (PasswordHasher ASP.NET Core) |
| Realtime  | socket.io                    | SignalR `/ws`                              |
| Streaming | SSE                          | SSE server→client                          |

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

## Testes

```bash
dotnet test OpenWebUI.slnx
# 311 testes de API + 7 de cliente (NUnit)

# Cobertura (Coverlet, exclui Client WASM, código gerado e assemblies de teste)
dotnet test tests/OpenWebUI.Api.Tests \
  --collect:"XPlat Code Coverage" --settings coverlet.runsettings
```

| Camada | Linhas | Branches |
|--------|--------|----------|
| Domain | 100% | 100% |
| Application | 100% | 100% |
| Infrastructure | 96,9% | 92,2% |
| Api | 96,7% | 83,6% |
| **Total** | **97,4%** | **88,5%** |

## Documentação

- [`docs/MIGRACAO-DOTNET.md`](docs/MIGRACAO-DOTNET.md) — mapa de paridade upstream → .NET
- [`docs/architecture/architecture.md`](docs/architecture/architecture.md) — diagrama Mermaid da arquitetura
- [`docs/specs/`](docs/specs/) — SPEC SDDs entregues (Epic gap-analysis-20261001)
- [`CHANGELOG.md`](CHANGELOG.md)

## Licença

BSD-3-Clause — ver [`LICENSE`](LICENSE).

# Open WebUI — .NET

Migração do [Open WebUI](https://github.com/open-webui/open-webui) para
**.NET 10 · C# 14 · Blazor WebAssembly** (SvelteKit + FastAPI → ASP.NET Core + Blazor).

## Estrutura (Clean Architecture)

```
├── OpenWebUI.slnx
├── src/
│   ├── OpenWebUI.Domain/          # Entidades de domínio (sem dependências)
│   ├── OpenWebUI.Application/     # Contratos/DTOs compartilhados
│   ├── OpenWebUI.Infrastructure/  # EF Core (AppDbContext), serviços
│   │                              # (JWT, providers Ollama/OpenAI, config)
│   ├── OpenWebUI.Api/             # Minimal APIs + hosting do Blazor WASM
│   └── OpenWebUI.Client/          # SPA Blazor WebAssembly (UI do chat)
└── tests/
    └── OpenWebUI.Api.Tests/       # Testes de integração NUnit
```

Dependências apontam para dentro: `Api → Infrastructure → Application → Domain`.
O `Client` consome apenas `Application` (contratos).

| Camada    | Original                     | Migrado                                    |
| --------- | ---------------------------- | ------------------------------------------ |
| Frontend  | SvelteKit + Tailwind         | Blazor WebAssembly                         |
| Backend   | Python FastAPI               | ASP.NET Core 10 (Minimal APIs)             |
| Banco     | SQLAlchemy + SQLite/Postgres | EF Core 10 + SQLite                        |
| Auth      | JWT + bcrypt                 | JWT + PBKDF2 (PasswordHasher ASP.NET Core) |
| Streaming | WebSocket/socket.io + SSE    | SSE server→client                          |

## Executar

Requisito: SDK do .NET 10.

```bash
dotnet restore OpenWebUI.slnx
dotnet run --project src/OpenWebUI.Api
# http://localhost:8080 — o primeiro usuário cadastrado vira admin
```

O servidor serve o Blazor WASM e a API na mesma origem (sem CORS).
Por padrão conecta em um Ollama local (`http://localhost:11434`); conexões
OpenAI são configuradas em **Configurações → Conexões** (somente admin).

### Configuração

| Variável                     | Padrão                          | Descrição                 |
| ---------------------------- | ------------------------------- | ------------------------- |
| `ConnectionStrings__Default` | `Data Source=data/openwebui.db` | Connection string EF Core |

O segredo JWT é gerado automaticamente e persistido na tabela de configuração.

## Docker

```bash
docker compose up -d        # app + Ollama em http://localhost:3000
docker compose up -d --no-deps openwebui   # só o app
```

Dados (SQLite + uploads) ficam no volume `openwebui`. Conexões com
provedores podem ser semeadas via `OLLAMA_BASE_URL`, `OPENAI_API_BASE_URLS`
e `OPENAI_API_KEYS` (separadas por `;`) na primeira execução.

## Testar

```bash
dotnet test OpenWebUI.slnx
```

## Documentação

- [docs/MIGRACAO-DOTNET.md](docs/MIGRACAO-DOTNET.md) — mapa de paridade com o
  upstream (o que está migrado, parcial e pendente) e roadmap.

## Licença

Este fork mantém os arquivos de licença do projeto original
(`LICENSE`, `LICENSE_HISTORY`, `LICENSE_NOTICE`).

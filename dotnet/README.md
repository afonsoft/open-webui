# Open WebUI — Migração para .NET 10 / Blazor WebAssembly

Porte da plataforma [Open WebUI](https://github.com/open-webui/open-webui) para a stack
**.NET 10 · C# 14 · Blazor WebAssembly**, mantendo o mesmo modelo arquitetural:
um único processo que serve a UI e a API, conectando-se a provedores de IA
(Ollama e APIs compatíveis com OpenAI).

## Estrutura

```
dotnet/
├── OpenWebUI.slnx
├── src/
│   ├── OpenWebUI.Shared/   # DTOs e contratos compartilhados (net10.0)
│   ├── OpenWebUI.Client/   # SPA Blazor WebAssembly (UI do chat)
│   └── OpenWebUI.Server/   # ASP.NET Core Minimal APIs + hosting do WASM
└── tests/
    └── OpenWebUI.Server.Tests/  # Testes de integração NUnit
```

| Camada | Original | Migrado |
|--------|----------|---------|
| Frontend | SvelteKit + Tailwind | Blazor WebAssembly |
| Backend | Python FastAPI | ASP.NET Core 10 (Minimal APIs) |
| Banco | SQLAlchemy + SQLite/Postgres | EF Core 10 + SQLite |
| Auth | JWT + bcrypt | JWT + PBKDF2 (PasswordHasher ASP.NET Core) |
| Streaming | WebSocket/socket.io + SSE | SSE server→client |

## Executando

Requisito: .NET SDK 10.0+.

```bash
cd dotnet
dotnet run --project src/OpenWebUI.Server
```

Abra http://localhost:8080 — o primeiro usuário cadastrado vira **admin**.

O servidor serve o Blazor WASM e a API na mesma origem (sem CORS).
Por padrão conecta em um Ollama local (`http://localhost:11434`); conexões
OpenAI são configuradas em **Configurações → Conexões** (somente admin).

### Configuração

| Variável | Padrão | Descrição |
|----------|--------|-----------|
| `ConnectionStrings__Default` | `Data Source=data/openwebui.db` | Connection string EF Core |

O segredo JWT é gerado automaticamente e persistido na tabela de configuração.

## Testes

```bash
cd dotnet
dotnet test
```

## Funcionalidades migradas

- Cadastro/login com JWT (primeiro usuário = admin)
- CRUD de chats com persistência e busca por título
- Chat com streaming SSE token a token (Markdown sanitizado)
- Seleção de modelo agregando Ollama (`/api/tags`) e OpenAI (`/models`)
- Proxy de completions: Ollama (`/api/chat` → chunks OpenAI) e OpenAI
  (`/chat/completions` pass-through)
- Configurações de conexões (admin) com preservação de chaves
- Tema claro/escuro, layout responsivo estilo Open WebUI
- Rotas espelhadas: `/api/v1/auths`, `/api/v1/chats`, `/api/models`,
  `/api/chat/completions`

## Roadmap (não migrado nesta fase)

RAG/documentos, Knowledge, Tools/Functions/Pipes, web search, Channels, Notes,
Calendário, RBAC granular/grupos, multi-usuário em tempo real (socket.io),
geração de título por LLM, edição/branching de mensagens, voice/video, TTS/STT,
memória persistente, artifacts, migrações EF (hoje `EnsureCreated`), Docker.

Ver `docs/MIGRACAO-DOTNET.md` para o mapeamento completo.

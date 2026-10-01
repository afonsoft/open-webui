# Migração Open WebUI → .NET 10 / Blazor WebAssembly

Documento de acompanhamento da migração de tecnologia do Open WebUI
(SvelteKit + FastAPI/Python) para **.NET 10 · C# 14 · Blazor WebAssembly**.

> Revisão pós-expansão (out/2026): inventário refeito contra o upstream
> `open-webui/open-webui` (~v0.11.x, ~600 endpoints, ~48 rotas de página,
> ~250 chaves de `DEFAULT_CONFIG`). A tabela abaixo reflete o estado real
> implementado em `dotnet/` nesta branch.

## Arquitetura alvo

```
┌─────────────────────────────────────────────┐
│ OpenWebUI.Server (ASP.NET Core 10)          │
│  ├── serve OpenWebUI.Client (Blazor WASM)   │
│  ├── /api/v1/*  (auth, chats, configs)      │
│  ├── /api/models, /api/chat/completions     │
│  └── EF Core SQLite (data/openwebui.db)     │
└─────────────────────────────────────────────┘
              │                │
        Ollama /api/*    OpenAI /v1/*
```

## Paridade por área (inventário → dotnet/)

| Área (Open WebUI original)             | Status      | Observação                                                |
| -------------------------------------- | ----------- | --------------------------------------------------------- | ------- |
| Auth (signup/signin/signout, JWT)      | ✅ Migrado  | `/api/v1/auths/*` completo, primeiro usuário vira admin   |
| Papéis pending/user/admin              | ✅ Migrado  | `DEFAULT_USER_ROLE` configurável no Admin                 |
| Aprovação de usuários pendentes        | ✅ Migrado  | Página `/admin` aprova/rebaixa/remove                     |
| Perfil (nome, imagem, senha, fuso)     | ✅ Migrado  | `/update/profile`, `/update/password`, `/update/timezone` |
| Chaves de API `sk-*`                   | ✅ Migrado  | POST/GET/DELETE `/api_key` + middleware Bearer sk-        |
| Chats CRUD + busca                     | ✅ Migrado  | Título e conteúdo de mensagens                            |
| Fixar / arquivar chats                 | ✅ Migrado  | `/pin`, `/archive`, listas dedicadas                      |
| Pastas de conversas                    | ✅ Migrado  | `/api/v1/folders`, sidebar agrupada                       |
| Tags de chats                          | ✅ Migrado  | `/api/v1/chats/{id}/tags`                                 |
| Compartilhamento público `/s/{id}`     | ✅ Migrado  | ShareId + página pública sem login                        |
| Clone de chat                          | ✅ Migrado  | `POST /{id}/clone`                                        |
| Exportar / importar chats              | ✅ Migrado  | `/all/db`, `/import`                                      |
| Mensagens: editar / apagar restante    | ✅ Migrado  | `/{id}/messages/{mid}` POST/DELETE                        |
| Regenerar resposta                     | ✅ Migrado  | Por mensagem                                              |
| Copiar mensagem                        | ✅ Migrado  | Ação no bubble                                            |
| Avaliação (👍/👎)                      | ✅ Migrado  | `/api/v1/evaluations/feedback`                            |
| Título automático via LLM              | ✅ Migrado  | `/api/v1/tasks/title/completions`                         |
| Follow-ups sugeridos                   | ✅ Migrado  | `/tasks/follow_up/completions`                            |
| Tags automáticas                       | ✅ Migrado  | `/tasks/tags/completions`                                 |
| Upload de arquivos + contexto          | 🟡 Parcial  | Texto extraído injetado no prompt; sem RAG vetorial       |
| Prompts `/comando`                     | ✅ Migrado  | `/api/v1/prompts`, autocomplete no input                  |
| Modelos personalizados do workspace    | ✅ Migrado  | `/api/v1/models`, system prompt + params                  |
| Memórias persistentes                  | ✅ Migrado  | `/api/v1/memories`, injetadas no contexto                 |
| Notas                                  | ✅ Migrado  | `/api/v1/notes`, página `/notes`                          |
| Lista agregada de modelos              | ✅ Migrado  | Ollama + OpenAI + custom models                           |
| Conexões (admin)                       | ✅ Migrado  | Chaves nunca retornadas à UI                              |
| `/api/config` + `/health`              | ✅ Migrado  | Feature flags públicas                                    |
| Exportar/importar config               | ✅ Migrado  | `/api/v1/configs/export                                   | import` |
| Tema claro/escuro                      | ✅ Migrado  | Persistido em localStorage                                |
| RAG / Knowledge / vector store         | ⬜ Pendente | 9 vector DBs no original                                  |
| Web search RAG                         | ⬜ Pendente | Stub de query generation existe                           |
| Tools / Functions / Pipes / Filters    | ⬜ Pendente | Sistema de plugins Python                                 |
| Channels (chat em grupo)               | ⬜ Pendente |                                                           |
| Groups / RBAC granular                 | ⬜ Pendente | Somente papel admin/user/pending                          |
| OAuth / LDAP / SAML / SCIM             | ⬜ Pendente |                                                           |
| Voice / STT / TTS / Call               | ⬜ Pendente |                                                           |
| Image generation                       | ⬜ Pendente |                                                           |
| Code execution (Pyodide/Open Terminal) | ⬜ Pendente |                                                           |
| Socket.io / realtime multiusuário      | ⬜ Pendente | Avaliar SignalR                                           |
| PWA / offline                          | ⬜ Pendente |                                                           |
| i18n                                   | ⬜ Pendente | UI em pt-BR hardcoded                                     |
| Analytics / métricas                   | ⬜ Pendente |                                                           |
| Automations / calendar / pipelines     | ⬜ Pendente |                                                           |
| Migrações EF Core                      | 🟡 Parcial  | `SchemaBootstrap` incremental (colunas/tabelas)           |
| Docker / deploy dedicado               | ⬜ Pendente |                                                           |

## Rotas de página (frontend)

| Original (SvelteKit)                                      | Blazor                                    | Status |
| --------------------------------------------------------- | ----------------------------------------- | ------ |
| `/`                                                       | `/`                                       | ✅     |
| `/c/{id}`                                                 | `/c/{ChatId}`                             | ✅     |
| `/auth`                                                   | `/auth`                                   | ✅     |
| `/s/{id}`                                                 | `/s/{ShareId}`                            | ✅     |
| `/admin` (users, evals, settings)                         | `/admin` (usuários) + modal Settings      | 🟡     |
| `/workspace` (models, prompts, knowledge, tools, files)   | `/workspace` (prompts, modelos, arquivos) | 🟡     |
| `/notes`                                                  | `/notes`                                  | ✅     |
| Arquivadas (modal/menu)                                   | `/archived`                               | ✅     |
| `/channels/*`, `/playground`, `/calendar`, `/automations` | —                                         | ⬜     |

## Decisões de design

- **`SchemaBootstrap`** em vez de EF Migrations — evolui o SQLite incrementalmente
  (colunas novas via `ALTER`, tabelas via `CREATE TABLE IF NOT EXISTS`, rebuild da
  `ChatMessages` para PK composta `ChatId+Id`). Alembic → EF Migrations quando
  o esquema estabilizar.
- **Streaming SSE** no lugar de WebSocket — suficiente para token streaming;
  SignalR reservado para features realtime futuras.
- **Chaves de API somente no servidor** — a UI nunca recebe segredos;
  `ConnectionsConfigResponse` expõe apenas `OpenAiKeyConfigured`.
- **Segredo JWT persistido** na tabela `config` (mesmo padrão do original:
  `WEBUI_SECRET_KEY` gerado na primeira execução).
- **Config admin persistida** em `admin.config` no `ConfigService` —
  espelha `WEBUI_*` / `ENABLE_*` do `DEFAULT_CONFIG` para os toggles migrados.

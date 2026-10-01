# Migração Open WebUI → .NET 10 / Blazor WebAssembly

Documento de acompanhamento da migração de tecnologia do Open WebUI
(SvelteKit + FastAPI/Python) para **.NET 10 · C# 14 · Blazor WebAssembly**.

> Revisão pós-expansão (out/2026): inventário refeito contra o upstream
> `open-webui/open-webui` (~v0.11.x, ~600 endpoints, ~48 rotas de página,
> ~250 chaves de `DEFAULT_CONFIG`). O código legado foi removido — o repo
> agora contém apenas a migração .NET, organizada em Clean Architecture
> (`src/OpenWebUI.{Domain,Application,Infrastructure,Api,Client}`).
> A tabela abaixo reflete o estado real implementado.

## Arquitetura alvo

```
┌─────────────────────────────────────────────┐
│ OpenWebUI.Api (ASP.NET Core 10)             │
│  ├── serve OpenWebUI.Client (Blazor WASM)   │
│  ├── /api/v1/*  (auth, chats, configs)      │
│  ├── /api/models, /api/chat/completions     │
│  └── EF Core SQLite (data/openwebui.db)     │
└─────────────────────────────────────────────┘
              │                │
        Ollama /api/*    OpenAI /v1/*
```

## Paridade por área (inventário → src/)

| Área (Open WebUI original)             | Status      | Observação                                                |
| -------------------------------------- | ----------- | --------------------------------------------------------- |
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
| RAG / Knowledge / vector store         | ✅ Migrado  | Store SQLite + cosseno; embeddings Ollama/OpenAI       |
| Web search RAG                         | ⬜ Pendente | Stub de query generation existe                           |
| Tools / Functions / Pipes / Filters    | 🟡 Parcial  | Tools HTTP com function calling (loop no servidor); Pipes/Filters não suportados |
| Channels (chat em grupo)               | ✅ Migrado  | SignalR /ws + mensagens + @modelo + typing/presence   |
| Groups / RBAC granular                 | ✅ Migrado  | `Group`/`GroupMember` + flags workspace/sharing/chat      |
| OAuth / LDAP / SAML / SCIM             | 🟡 Parcial  | OAuth (Google/GitHub/Microsoft/OIDC) + LDAP bind; SAML/SCIM pendentes |
| Voice / STT / TTS / Call               | ⬜ Pendente |                                                           |
| Image generation                       | 🟡 Parcial  | OpenAI Images; botão na mensagem + config admin; sem ComfyUI/A1111 |
| Code execution (Pyodide/Open Terminal) | 🟡 Parcial  | JS em Web Worker + Python via Pyodide WASM; botão Executar em blocos |
| Socket.io / realtime multiusuário      | ⬜ Pendente | Avaliar SignalR                                           |
| PWA / offline                          | ⬜ Pendente |                                                           |
| i18n                                   | 🟡 Parcial  | pt-BR/en-US com troca sem reload; backend não traduzido   |
| Analytics / métricas                   | 🟡 Parcial  | `/api/v1/analytics` admin-only + aba Analytics em /admin |
| Automations / calendar / pipelines     | ⬜ Pendente |                                                           |
| Migrações EF Core                      | ✅ Migrado  | `DatabaseMigrator` + EF Migrations; baseline de bases legadas |
| Docker / deploy dedicado               | ⬜ Pendente |                                                           |

## Rotas de página (frontend)

| Original (SvelteKit)                                      | Blazor                                    | Status |
| --------------------------------------------------------- | ----------------------------------------- | ------ |
| `/`                                                       | `/`                                       | ✅     |
| `/c/{id}`                                                 | `/c/{ChatId}`                             | ✅     |
| `/auth`                                                   | `/auth`                                   | ✅     |
| `/s/{id}`                                                 | `/s/{ShareId}`                            | ✅     |
| `/admin` (users, evals, settings)                         | `/admin` (usuários, grupos, analytics, avaliações, flags) | ✅     |
| `/workspace` (models, prompts, knowledge, tools, files)   | `/workspace` (prompts, modelos, arquivos, tools) | 🟡     |
| `/notes`                                                  | `/notes`                                  | ✅     |
| Arquivadas (modal/menu)                                   | `/archived`                               | ✅     |
| `/channels/*`                                            | `/channels/{id}`                          | ✅     |
| `/playground`                                             | `/playground` (sem persistir chat)        | ✅     |
| `/calendar`, `/automations`                               | —                                         | ⬜     |

## Decisões de design

- **EF Core Migrations** — `DatabaseMigrator` aplica `Migrate()` no startup e faz
  baseline de bases legadas criadas sem histórico (marca migrações como aplicadas
  em `__EFMigrationsHistory` sem recriar tabelas nem perder dados).
- **Streaming SSE** no lugar de WebSocket — suficiente para token streaming;
  SignalR reservado para features realtime futuras.
- **Chaves de API somente no servidor** — a UI nunca recebe segredos;
  `ConnectionsConfigResponse` expõe apenas `OpenAiKeyConfigured`.
- **Segredo JWT persistido** na tabela `config` (mesmo padrão do original:
  `WEBUI_SECRET_KEY` gerado na primeira execução).
- **Config admin persistida** em `admin.config` no `ConfigService` —
  espelha `WEBUI_*` / `ENABLE_*` do `DEFAULT_CONFIG` para os toggles migrados.

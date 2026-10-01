# Migração Open WebUI → .NET 10 / Blazor WebAssembly

Documento de acompanhamento da migração de tecnologia do Open WebUI
(SvelteKit + FastAPI/Python) para **.NET 10 · C# 14 · Blazor WebAssembly**.

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

## Status por área

| Área (Open WebUI original) | Status | Observação |
|---|---|---|
| Auth (signup/signin/JWT) | ✅ Migrado | Primeiro usuário vira admin |
| Chats (CRUD, busca) | ✅ Migrado | Histórico linear (sem branching) |
| Chat UI + streaming | ✅ Migrado | SSE via `/api/chat/completions` |
| Modelos (lista agregada) | ✅ Migrado | Ollama + OpenAI |
| Conexões (admin) | ✅ Migrado | Chaves nunca retornadas à UI |
| Tema claro/escuro | ✅ Migrado | Persistido em localStorage |
| Edição de mensagens | ⬜ Pendente | |
| Regeneração | ✅ Migrado | Na última resposta |
| Branching de mensagens | ⬜ Pendente | Modelo `history` do original |
| Título automático via LLM | ⬜ Pendente | Hoje: primeiros 60 chars da 1ª msg |
| RBAC / grupos / permissões | ⬜ Pendente | Apenas papel admin/user |
| Aprovação de usuários (pending) | 🟡 Parcial | Papel existe; sem painel de aprovação |
| RAG / Knowledge / arquivos | ⬜ Pendente | 9 vector DBs no original |
| Web search RAG | ⬜ Pendente | Dezenas de provedores no original |
| Tools / Functions / Pipes / Filters | ⬜ Pendente | Sistema de plugins Python |
| Channels / Notes / Calendar | ⬜ Pendente | |
| Memória persistente | ⬜ Pendente | |
| Voice/STT/TTS/Call | ⬜ Pendente | |
| socket.io / realtime multiusuário | ⬜ Pendente | Avaliar SignalR |
| PWA / offline | ⬜ Pendente | |
| i18n | ⬜ Pendente | UI em pt-BR hardcoded |
| Painel admin completo | ⬜ Pendente | Só conexões por ora |
| Migrações de banco | ⬜ Pendente | `EnsureCreated` no lugar de Alembic |
| Docker / deploy | ⬜ Pendente | Dockerfile dedicado a fazer |
| Workspace (Modelos/Agentes/Prompts) | ⬜ Pendente | |
| OAuth/LDAP/SSO | ⬜ Pendente | |
| Code execution (Pyodide/Open Terminal) | ⬜ Pendente | |
| Artifacts / KV storage | ⬜ Pendente | |

## Mapeamento de rotas

| Original (FastAPI) | .NET |
|---|---|
| `POST /api/v1/auths/signup` | ✅ |
| `POST /api/v1/auths/signin` | ✅ |
| `GET /api/v1/auths/` | ✅ |
| `GET/POST /api/v1/chats/` | ✅ |
| `GET/POST/DELETE /api/v1/chats/{id}` | ✅ |
| `GET /api/models` | ✅ |
| `POST /api/chat/completions` | ✅ (SSE) |
| `GET/POST /api/v1/configs/*` | 🟡 somente `connections` |
| `/ollama/*`, `/openai/*` proxies | 🟡 via roteamento interno |
| demais routers | ⬜ |

## Decisões de design

- **EF Core `EnsureCreated`** em vez de migrações — suficiente para a fase de
  fundação; Alembic → EF Migrations entra quando o esquema estabilizar.
- **Streaming SSE** no lugar de WebSocket — HTTP/1.1-friendly e suficiente para
  token streaming; SignalR reservado para features realtime futuras.
- **Chaves de API somente no servidor** — a UI nunca recebe segredos;
  `ConnectionsConfigResponse` expõe apenas `OpenAiKeyConfigured`.
- **Segredo JWT persistido** na tabela `config` (mesmo padrão do original:
  `WEBUI_SECRET_KEY` gerado na primeira execução).

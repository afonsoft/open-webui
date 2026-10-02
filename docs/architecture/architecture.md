# Arquitetura — Open WebUI .NET

Migração do Open WebUI para .NET 10 / Blazor WebAssembly em Clean Architecture.
Epic de paridade `gap-analysis-20261001` (13 slices, PRs #29–#41) entregue.
ADRs das decisões-chave em [README.md](README.md).

## Visão geral

```mermaid
flowchart LR
    subgraph Browser["Navegador (Blazor WebAssembly)"]
        UI["OpenWebUI.Client<br/>Pages + Components"]
        subgraph ClientJS["Interop JS (wwwroot/js)"]
            JS1["app.js — tema, scroll, copy, prompt"]
            JS2["audio.js — STT/TTS (Web Speech)"]
            JS3["codeexec.js — Web Worker JS"]
            JS4["py-worker.js — Python via Pyodide WASM"]
            SW["service-worker.js — shell cache (PWA)"]
        end
        ApiS["ApiService + ChatStreamService<br/>(HTTP + SSE)"]
        Hub["SignalR client → /ws"]
    end

    subgraph Server["ASP.NET Core 10 (OpenWebUI.Api)"]
        FH["UseForwardedHeaders<br/>(X-Forwarded-For/Proto/Host)"]
        EP["Minimal APIs<br/>Endpoints/*.cs<br/>(auths, chats, users, files,<br/>models, prompts, memories,<br/>notes, folders, tools,<br/>knowledge, evaluations,<br/>images, analytics,<br/>automations, configs, tasks,<br/>oauth, groups)"]
        WS["SignalR Hub /ws<br/>(channels realtime)"]
        AuthMW["JWT Bearer + sk-* keys"]
        OAuth["OAuth/OIDC + LDAP"]
        Sched["AutomationScheduler<br/>(BackgroundService, 15s)"]
    end

    subgraph Infra["OpenWebUI.Infrastructure"]
        DB["AppDbContext (EF Core + Migrations)"]
        Prov["ProviderService<br/>Ollama / OpenAI / tool loop"]
        Emb["EmbeddingService + RagService<br/>(retrieval vetorial)"]
        Img["ImageGenService<br/>(OpenAI Images)"]
        Cfg["ConfigService (kv)"]
    end

    subgraph Domain["OpenWebUI.Domain / Application"]
        Ent["Entities (~20 agregados)"]
        Dto["Contracts/DTOs"]
    end

    subgraph Store["Disco (fora do wwwroot — nunca servidos)"]
        Vol[("data/openwebui.db<br/>data/uploads/{user}/")]
    end

    UI --> ApiS --> EP
    UI --> Hub --> WS
    UI -.-> ClientJS
    FH --> EP
    EP --> AuthMW
    EP --> DB
    EP --> Prov
    EP --> Img
    EP --> Emb
    Sched --> Prov
    DB --> Ent
    DB --> Vol
    Prov --> Dto
    EP --> Cfg
    WS --> DB

    Prov <--> Ext1["Ollama / OpenAI-compat"]
    Img <--> Ext2["OpenAI Images API"]
    Emb <--> Ext1
    OAuth <--> Ext3["Google / GitHub / Microsoft / OIDC / LDAP"]
```

## Camadas (Clean Architecture)

| Camada | Projeto | Conteúdo | Depende de |
|--------|---------|----------|------------|
| Domain | `src/OpenWebUI.Domain` | Entidades EF (User, Chat, Channel, Knowledge, Tool, Automation…) | — |
| Application | `src/OpenWebUI.Application` | Contracts/DTOs e interfaces de serviço | Domain |
| Infrastructure | `src/OpenWebUI.Infrastructure` | `AppDbContext`, EF Migrations, serviços (JWT, providers, embeddings, imagens, automations, LDAP) | Application |
| Api | `src/OpenWebUI.Api` | Host, endpoints mínimos, SignalR, hosting do WASM | Infrastructure |
| Client | `src/OpenWebUI.Client` | Blazor WASM (pages, components, ApiService, LocalizationService) | Application |

## Sequência — chat completion com RAG + tools

```mermaid
sequenceDiagram
    participant U as Client (Blazor)
    participant A as OpenWebUI.Api
    participant S as Infra Services
    participant P as Provider (Ollama/OpenAI)
    participant T as Tool URL (server-side)

    U->>A: POST /api/chat/completions (JWT/sk-*)
    A->>S: resolve custom model + memórias + anexos
    S->>S: RagService: embeddings → top-k trechos
    A->>P: POST /chat/completions (stream)
    loop tool calling (máx. 5 rodadas)
        P-->>A: tool_calls
        A->>T: POST tool.url (args) — URL nunca sai do servidor
        T-->>A: resultado → injetado como tool message
        A->>P: continua stream
    end
    P-->>U: SSE tokens → MessageBubble
    A->>S: persiste Chat + Messages (PK composta ChatId,Id)
    A-->>U: follow-ups + título (tasks endpoints)
```

## Deploy

```mermaid
flowchart TB
    subgraph Proxy["Reverse proxy (nginx/Cloudflare)"]
        RP["TLS termination<br/>X-Forwarded-For/Proto/Host"]
    end
    subgraph App["container openwebui"]
        Api["OpenWebUI.Api :8080"]
        Vol[("/app/data (volume)<br/>openwebui.db + uploads/")]
        Api --- Vol
    end
    Ollama["ollama (opcional)<br/>OLLAMA_BASE_URL"]

    Browser["Navegador"] -->|"HTTPS + WSS"| RP --> Api
    Api --> Ollama
```

Atrás de proxy o app precisa de `UseForwardedHeaders` — sem ele `Request.Scheme`
devolve `http` e o `redirect_uri` do OAuth sai errado (mesmo padrão do
`agent-harness`). O diretório `data/` fica fora do `wwwroot`: nem
`MapStaticAssets` nem `MapFallbackToFile` o expõem.

## Decisões-chave

- **Persistência**: SQLite via EF Core Migrations (`DatabaseMigrator` faz
  baseline de `webui.db` legadas). Chave composta `(ChatId, Id)` em mensagens.
- **Segredos**: chaves de provider ficam no servidor (`ConfigEntry` kv,
  mascaradas na API); URLs de tools nunca retornam ao cliente.
- **Realtime**: SignalR `/ws` para canais — `message:new`, `typing`, `presence`.
- **RAG**: embeddings por provider + busca por cosseno em SQLite
  (sem vector store externo).
- **Voz/execução**: Web Speech API (STT/TTS), Web Worker JS e Pyodide WASM —
  tudo client-side, degradando silenciosamente.
- **PWA**: manifest + service worker do shell; `Cache-Control: no-cache` no
  documento para evitar loop de reload.
- **i18n**: dicionários `wwwroot/i18n/{lang}.json` carregados no boot do WASM,
  troca de idioma sem reload.
- **CI/CD**: `ci-build-test` (coverage gate + ratchet), `code-quality`
  (Qodana/Sonar), `security-scan` (CodeQL/Trivy), `release` (GHCR + binários).

## Fora de escopo (deferido nos SPECs / gap-analysis)

SAML/SCIM, ComfyUI/A1111, web search RAG (engines de busca), Pipes/Filters,
Jupyter/Open Terminal, Whisper/Kokoro TTS/STT remoto, VAD, DM channels,
threads/reações em canais, arena models, notificações webhook, routers de
passthrough `/ollama/*` `/openai/*`, Postgres, Redis backplane (multi-instância),
comunidade openwebui.com.

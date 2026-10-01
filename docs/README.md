# Documentação do Sistema

## Overview

Open WebUI portado para **.NET 10 / C# 14 / Blazor WebAssembly** — mesma interface, funcionalidades e configuração do upstream `open-webui/open-webui`, com backend ASP.NET Core Minimal APIs substituindo o FastAPI e frontend Blazor WASM substituindo o Svelte.

## Arquitetura

Clean Architecture em 5 projetos:

```
Api ──▶ Infrastructure ──▶ Application ──▶ Domain
Client ──▶ Application
```

- **Domain**: entidades (usuários, chats, mensagens, pastas, arquivos, modelos custom, memórias, notas, avaliações, conexões).
- **Application**: contratos/DTOs e interfaces de serviços.
- **Infrastructure**: EF Core + SQLite (`webui.db`, schema evoluído por EF Migrations com baseline de bases legadas), JWT, providers (Ollama, OpenAI-compatível), seed de conexões por env.
- **Api**: Minimal APIs (`/api/v1/*`, `/api/config`) + hosting do WASM via `MapStaticAssets`.
- **Client**: Blazor WASM com Tailwind v4 (tema upstream, `.dark` class, Inter).

## Estrutura de diretórios

| Dir | Conteúdo |
|---|---|
| `src/` | 5 projetos .NET |
| `tests/` | `OpenWebUI.Api.Tests` (NUnit) |
| `docs/` | documentação do sistema + `MIGRACAO-DOTNET.md` |
| `.claude/` | harness de agentes (rules, agents, skills, memory) |
| `.devin/` | `blueprint.yaml`, `config.json`, `skills/` (catálogo afonsoft/skills) |
| `.specs/` | SPECs SDD em andamento |

## Quick Start

```bash
dotnet run --project src/OpenWebUI.Api   # http://localhost:8080
# ou
docker compose up -d                    # http://localhost:3000
```

Primeiro usuário registrado vira admin. Conexões de providers podem ser semeadas por env (`OLLAMA_BASE_URL`, `OPENAI_API_KEY`, ...).

## Referências

- [technologies.md](./technologies.md) — tecnologias e versões
- [packages.md](./packages.md) — dependências NuGet
- [plugins.md](./plugins.md) — integrações e extensões
- [features.md](./features.md) — funcionalidades implementadas
- [api.md](./api.md) — endpoints da API
- [MIGRACAO-DOTNET.md](./MIGRACAO-DOTNET.md) — paridade com o upstream

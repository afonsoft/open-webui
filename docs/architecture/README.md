# Arquitetura

Documentos que descrevem e decidem a arquitetura do Open WebUI .NET.

## ADRs

| ADR | Decisão |
|-----|---------|
| [AD-0001](AD-0001-clean-architecture.md) | Clean Architecture em 5 projetos (Domain → Application → Infrastructure → Api; Client WASM referencia só Application) |
| [AD-0002](AD-0002-sqlite-ef-migrations.md) | SQLite + EF Core Migrations com baseline de bases legadas |
| [AD-0003](AD-0003-server-held-secrets.md) | Segredos somente no servidor (chaves de provider, URLs de tools) |
| [AD-0004](AD-0004-realtime-signalr.md) | Realtime via SignalR `/ws`; SSE para streaming de chat |
| [AD-0005](AD-0005-client-side-media-exec.md) | Voz e execução de código no cliente (Web Speech, Web Worker, Pyodide WASM) |

## Diagramas e design

- [architecture.md](architecture.md) — visão geral do sistema, camadas, sequência do chat e deploy (Mermaid)
- [../MIGRACAO-DOTNET.md](../MIGRACAO-DOTNET.md) — mapa de paridade com o upstream

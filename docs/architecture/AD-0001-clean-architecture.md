# AD-0001 — Clean Architecture em 5 projetos

## Context

A migração do Open WebUI (FastAPI + SvelteKit monolito) precisava de uma estrutura
.NET que separasse domínio, contratos, infraestrutura e apresentação, mantendo o
cliente Blazor WASM desacoplado do servidor.

## Decision

Cinco projetos com dependências apenas para dentro:

- `OpenWebUI.Domain` — entidades EF, sem dependências.
- `OpenWebUI.Application` — contracts/DTOs e interfaces.
- `OpenWebUI.Infrastructure` — `AppDbContext`, migrations, serviços.
- `OpenWebUI.Api` — host ASP.NET Core, Minimal APIs, SignalR, hosting do WASM.
- `OpenWebUI.Client` — Blazor WASM; referencia **somente** Application
  (o navegador nunca toca Infrastructure).

## Consequences

- Positive: camadas testáveis isoladamente; o WASM não carrega EF Core/provedores;
  direção de dependência impedida pelo grafo de projetos.
- Trade-off: endpoints vivem em `Api/Endpoints/*.cs` como funções de borda —
  lógica compartilhada precisa ser promovida a Application/Infrastructure
  conscientemente.

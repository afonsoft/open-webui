# AD-0004 — Realtime via SignalR `/ws`; SSE para streaming de chat

## Context

O upstream usa Socket.IO para canais realtime e SSE/WebSocket para completions.
No .NET a escolha era entre WebSocket cru, SignalR ou SSE.

## Decision

- **SignalR** `Hub` em `/ws` para canais: eventos `message:new`, `typing`,
  `presence` — escala de 1 usuário a N membros sem loop de polling.
- **SSE** para `/api/chat/completions` e completions de canal — token streaming
  unidirecional não precisa de canal full-duplex.

## Consequences

- Positive: reconexão e negociação de transporte de graça no cliente Blazor;
  SSE fica simples e debugável com `curl`.
- Trade-off: SignalR in-process (single-instance); scale-out exigiria
  backplane Redis — documentado como pendência multi-instância.

# AD-0005 — Voz e execução de código no cliente

## Context

Upstream oferece STT/TTS por provider remoto (Whisper, OpenAI, Azure) e execução
de código via Jupyter/Open Terminal — ambos exigem infraestrutura extra.

## Decision

Tudo no navegador, degradando silenciosamente:

- **STT/TTS/Call** — Web Speech API (`js/audio.js`): sem custo de provider,
  sem envio de áudio ao servidor.
- **JS** — Web Worker (`js/codeexec.js`).
- **Python** — Pyodide WASM (`js/py-worker.js`).

## Consequences

- Positive: zero infra extra; funciona offline dentro do shell PWA; botões se
  escondem quando o navegador não suporta.
- Trade-off: cobertura de navegador (STT depende de Chromium/Safari);
  sem Whisper/server-side STT e sem Jupyter — registrados como
  pendências de paridade, não como bugs.

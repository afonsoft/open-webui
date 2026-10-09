# Funcionalidades

Status detalhado de paridade com o upstream: ver `docs/MIGRACAO-DOTNET.md`.

## Implementado

- **Auth**: registro/login JWT, chaves de API `sk-*`, primeiro usuário = admin, página de auth fiel ao upstream.
- **Chat**: conversas com streaming (SSE), anexos, avaliação 👍/👎, edição e regeneração por mensagem, follow-ups, autocomplete de prompts, compartilhamento público (`/s/{id}`).
- **Sidebar**: fixados, pastas, menu de contexto por chat, busca, seções, rail recolhida, menu do usuário.
- **Modelos**: seletor dropdown agrupado por provider; modelos custom; conexões Ollama/OpenAI gerenciadas ou semeadas por env.
- **Usuários**: perfil, senha, chaves; admin de usuários (`/admin`).
- **Workspace**: prompts, modelos custom, arquivos (`/workspace`).
- **Notas** (`/notes`), **Memórias**, **Arquivados** (`/archived`).
- **Tarefas**: geração de título, follow-ups e tags via LLM (`/api/v1/tasks`).
- **IDE/LSP**: diagnósticos (squiggles + painel Problems), hover e símbolos via language servers por workdir — `Lsp:*` config (defaults csharp-ls, typescript-language-server, pylsp, vscode-json-languageserver; binários ausentes degradam para `unavailable`).
- **Tema**: dark/light/system via `.dark` + `localStorage webui.theme`.

## Parcial / pendente (roadmap)

RAG/Knowledge, channels, groups/RBAC granular, OAuth/LDAP, voice, image gen, code execution, realtime, i18n, PWA, analytics, automations — ver tabela de paridade.

#!/usr/bin/env bash
# Bloqueia push/commit direto em branches protegidas (main, master, develop).
# Recebe o payload do hook em stdin; inspeciona o comando Exec.
set -euo pipefail

payload="$(cat)"
command="$(printf '%s' "$payload" | grep -o '"command"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed 's/.*:[[:space:]]*"//; s/"$//')"

case "$command" in
  *"push"*"main"*|*"push"*"master"*|*"push"*"develop"*)
    echo "BLOCKED: push direto para branch protegida (main/master/develop). Use PR." >&2
    exit 2
    ;;
esac
exit 0

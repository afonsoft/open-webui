#!/usr/bin/env bash
# update-dockerhub-overview.sh — publica a descrição curta + o overview
# (docs/dockerhub/overview.md) na página do repositório no Docker Hub.
#
# Requer um Docker Hub Personal Access Token em DOCKERHUB_TOKEN com escopo
# Read/Write/Delete — o Hub trata edições de metadados do repo como operação
# admin, então um PAT Read&Write comum é rejeitado.
#   https://app.docker.com/settings/personal-access-tokens
#
# Uso:
#   DOCKERHUB_TOKEN=dckr_pat_xxx ./scripts/update-dockerhub-overview.sh
#   DOCKERHUB_USERNAME=afonsoft DOCKERHUB_REPO=open-webui \
#     OVERVIEW_FILE=docs/dockerhub/overview.md \
#     SHORT_DESCRIPTION="..." \
#     DOCKERHUB_TOKEN=... ./scripts/update-dockerhub-overview.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

DOCKERHUB_USERNAME="${DOCKERHUB_USERNAME:-afonsoft}"
DOCKERHUB_REPO="${DOCKERHUB_REPO:-open-webui}"
OVERVIEW_FILE="${OVERVIEW_FILE:-$REPO_ROOT/docs/dockerhub/overview.md}"
SHORT_DESCRIPTION="${SHORT_DESCRIPTION:-Open WebUI em .NET 10 — Blazor WASM + ASP.NET Core + plataforma de agentes, um container.}"

: "${DOCKERHUB_TOKEN:?Defina DOCKERHUB_TOKEN com um PAT do Docker Hub (escopo Read/Write/Delete)}"
[[ -f "$OVERVIEW_FILE" ]] || { echo "overview não encontrado: $OVERVIEW_FILE" >&2; exit 1; }
((${#SHORT_DESCRIPTION} <= 100)) || { echo "SHORT_DESCRIPTION excede o limite de 100 chars do Docker Hub" >&2; exit 1; }

echo "==> Login no Docker Hub como $DOCKERHUB_USERNAME"
JWT="$(python3 - "$DOCKERHUB_USERNAME" <<'PY'
import json, os, sys, urllib.request
req = urllib.request.Request(
    "https://hub.docker.com/v2/users/login",
    data=json.dumps({"username": sys.argv[1],
                     "password": os.environ["DOCKERHUB_TOKEN"]}).encode(),
    headers={"Content-Type": "application/json"},
)
print(json.load(urllib.request.urlopen(req))["token"])
PY
)"

payload="$(mktemp)"
trap 'rm -f "$payload"' EXIT
python3 - "$OVERVIEW_FILE" "$SHORT_DESCRIPTION" > "$payload" <<'PY'
import json, sys
print(json.dumps({
    "description": sys.argv[2],
    "full_description": open(sys.argv[1], encoding="utf-8").read(),
}))
PY

echo "==> PATCH /v2/repositories/$DOCKERHUB_USERNAME/$DOCKERHUB_REPO"
response="$(curl -fsS -X PATCH \
  "https://hub.docker.com/v2/repositories/${DOCKERHUB_USERNAME}/${DOCKERHUB_REPO}/" \
  -H "Authorization: JWT $JWT" \
  -H 'Content-Type: application/json' \
  --data @"$payload")"
python3 - "$response" <<'PY'
import json, sys
d = json.loads(sys.argv[1])
print(f"updated {d['user']}/{d['name']} — description: {d['description']!r}, "
      f"overview: {len(d.get('full_description') or '')} chars")
PY

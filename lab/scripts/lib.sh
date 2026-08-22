#!/usr/bin/env bash
# Shared helpers for the lab scripts. Source this, do not execute it.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
LAB_DIR="${REPO_ROOT}/lab"

# shellcheck disable=SC1091
source "${LAB_DIR}/versions.env"

# Cluster name comes from the k3d config so there is one source of truth.
CLUSTER_NAME="$(awk '/^metadata:/{m=1;next} m && /^[[:space:]]+name:/{print $2; exit}' "${LAB_DIR}/k3d/cluster.yaml")"
KUBE_CONTEXT="k3d-${CLUSTER_NAME}"

log()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m  ✓\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m  !\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }

need() {
  command -v "$1" >/dev/null 2>&1 || die "$1 not found on PATH. Run: make preflight"
}

# Refuse to touch anything that is not the local lab cluster.
require_lab_context() {
  need kubectl
  local current
  current="$(kubectl config current-context 2>/dev/null || echo none)"
  [[ "${current}" == "${KUBE_CONTEXT}" ]] \
    || die "kubectl context is '${current}', expected '${KUBE_CONTEXT}'. Run: kubectl config use-context ${KUBE_CONTEXT}"
}

cluster_exists() {
  need k3d
  k3d cluster list --output json 2>/dev/null | grep -q "\"name\":\"${CLUSTER_NAME}\""
}

# Read one top-level key out of a flat apps/<name>/app.yaml.
# app.yaml is intentionally flat so both Argo CD and plain shell can read it.
app_field() {
  local file="$1" key="$2"
  sed -n "s/^${key}:[[:space:]]*\"\{0,1\}\([^\"#]*\)\"\{0,1\}[[:space:]]*$/\1/p" "${file}" \
    | head -1 | sed 's/[[:space:]]*$//'
}

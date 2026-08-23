#!/usr/bin/env bash
# Rebuild the entire lab from nothing and verify it.
#
#   make recover          rebuild on top of whatever exists (idempotent)
#   make recover FRESH=1  destroy the cluster first, then rebuild
#
# The recovery order is not arbitrary:
#
#   1. tooling      pinned kubectl/helm/k3d — the machine may be new
#   2. cluster      k3d from lab/k3d/cluster.yaml
#   3. platform     Argo CD, then Argo installs everything else from git
#   4. images       rebuild local images — git cannot restore these
#   5. apps         Argo syncs apps/ once the images exist
#   6. verify       prove it, do not assume it
#
# Step 4 is the one people forget. Manifests referencing an image that only
# ever existed on the old cluster will sit in ErrImagePull forever.

source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

SCRIPTS="${LAB_DIR}/scripts"
started="$(date +%s)"

if [[ "${FRESH:-0}" == "1" ]]; then
  warn "FRESH=1 — the existing cluster and ALL its data will be destroyed"
  warn "this includes Prometheus metrics and Loki logs (they live on cluster PVCs)"
  "${SCRIPTS}/cluster-down.sh"
fi

log "[1/6] Toolchain"
"${SCRIPTS}/preflight.sh"

log "[2/6] Cluster"
"${SCRIPTS}/cluster-up.sh"

log "[3/6] Platform (Argo CD, then Argo installs the rest from git)"
"${SCRIPTS}/bootstrap.sh"

log "[4/6] Locally-built images"
"${SCRIPTS}/images.sh"

log "[5/6] Waiting for applications to become healthy"
# Argo may have already tried and failed to pull images that only exist now.
# Nudge each app rather than waiting out the retry backoff.
for app in $(kubectl -n argocd get applications -o name 2>/dev/null); do
  kubectl -n argocd annotate "${app}" argocd.argoproj.io/refresh=hard --overwrite >/dev/null 2>&1 || true
done

deadline=$(( $(date +%s) + 600 ))
while true; do
  total="$(kubectl -n argocd get applications --no-headers 2>/dev/null | wc -l)"
  healthy="$(kubectl -n argocd get applications -o json 2>/dev/null \
    | python3 -c 'import json,sys;print(sum(1 for a in json.load(sys.stdin)["items"] if a.get("status",{}).get("health",{}).get("status")=="Healthy"))' 2>/dev/null || echo 0)"
  if [[ "${total}" -gt 0 && "${healthy}" == "${total}" ]]; then
    ok "${healthy}/${total} applications healthy"
    break
  fi
  if [[ "$(date +%s)" -gt "${deadline}" ]]; then
    warn "timed out with ${healthy}/${total} healthy — continuing to verification for the detail"
    break
  fi
  sleep 10
done

log "[6/6] Verification"
"${SCRIPTS}/verify.sh"

elapsed=$(( $(date +%s) - started ))
echo
ok "recovered in $((elapsed / 60))m $((elapsed % 60))s"
cat <<SUMMARY

  Portal     http://portal.${LAB_DOMAIN}:${LAB_HTTP_PORT}
  Argo CD    http://argocd.${LAB_DOMAIN}:${LAB_HTTP_PORT}    make argocd-password
  Grafana    http://grafana.${LAB_DOMAIN}:${LAB_HTTP_PORT}   make grafana-password

  Not restored by this script, because it never left the cluster:
    - Prometheus metric history and Loki logs (PVCs died with the cluster)
    - any password changed in a UI (Grafana resets to its seeded value)
    - anything created with kubectl that was never committed to git

SUMMARY

#!/usr/bin/env bash
# Install platform add-ons into the lab cluster. Idempotent — safe to re-run.
#
#   ingress-nginx   entry point for every app Ingress
#   metrics-server  verified (k3s ships it) — the chart's HPA needs it
#   Argo CD         reconciles apps/* from the git remote
#   lab-apps        ApplicationSet that turns apps/*/app.yaml into Applications

source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

need helm; need kubectl
require_lab_context

# ── metrics-server (bundled with k3s) ────────────────────────
log "Verifying metrics-server (bundled with k3s)"
if kubectl -n kube-system get deployment metrics-server >/dev/null 2>&1; then
  kubectl -n kube-system rollout status deployment/metrics-server --timeout=180s >/dev/null
  ok "metrics-server available — HPA (autoscaling.enabled) will work"
else
  die "metrics-server missing. k3s ships it by default; check that --disable=metrics-server is not set in lab/k3d/cluster.yaml"
fi

# ── ingress-nginx ────────────────────────────────────────────
log "Installing ingress-nginx ${INGRESS_NGINX_CHART_VERSION}"
helm repo add ingress-nginx https://kubernetes.github.io/ingress-nginx >/dev/null 2>&1 || true
helm repo update ingress-nginx >/dev/null
helm upgrade --install ingress-nginx ingress-nginx/ingress-nginx \
  --version "${INGRESS_NGINX_CHART_VERSION}" \
  --namespace ingress-nginx --create-namespace \
  --values "${LAB_DIR}/platform/ingress-nginx/values.yaml" \
  --wait --timeout 5m
ok "ingress-nginx ready"

# ── Argo CD ──────────────────────────────────────────────────
log "Installing Argo CD ${ARGOCD_CHART_VERSION}"
helm repo add argo https://argoproj.github.io/argo-helm >/dev/null 2>&1 || true
helm repo update argo >/dev/null
helm upgrade --install argocd argo/argo-cd \
  --version "${ARGOCD_CHART_VERSION}" \
  --namespace argocd --create-namespace \
  --values "${LAB_DIR}/platform/argocd/values.yaml" \
  --wait --timeout 10m
ok "Argo CD ready"

# ── app-of-apps ──────────────────────────────────────────────
log "Applying the lab-apps ApplicationSet"
kubectl apply -f "${LAB_DIR}/platform/bootstrap/applicationset.yaml"
ok "ApplicationSet applied — watching ${REPO_URL} (${REPO_REVISION}) for apps/*/app.yaml"

# ── Observability ────────────────────────────────────────────
if [[ "${LAB_SKIP_OBSERVABILITY:-0}" == "1" ]]; then
  warn "skipping observability (LAB_SKIP_OBSERVABILITY=1)"
else
  "${LAB_DIR}/scripts/observability.sh"
fi

echo
log "Lab is up"
cat <<SUMMARY

  Argo CD UI     http://argocd.${LAB_DOMAIN}:${LAB_HTTP_PORT}
  Grafana        http://grafana.${LAB_DOMAIN}:${LAB_HTTP_PORT}   (admin / admin)
  username       admin
  password       make argocd-password

  App URLs       http://<app>.${LAB_DOMAIN}:${LAB_HTTP_PORT}

  Argo CD reconciles from the GIT REMOTE, not your working tree:
    committed + pushed  ->  Argo syncs it (within ~60s, or: make sync APP=<name>)
    still uncommitted   ->  make deploy APP=<name>   (direct helm, for iteration)

SUMMARY

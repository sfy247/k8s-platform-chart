#!/usr/bin/env bash
# Bootstrap the lab platform. Idempotent — safe to re-run.
#
# Only ONE thing is installed imperatively: Argo CD itself. Something has to
# create the reconciler. Everything after that is declared in git and applied
# by Argo CD:
#
#   lab/platform/**/platform-app.yaml   ingress-nginx, Prometheus, Grafana,
#                                       Alertmanager, Loki, Alloy
#   lab/platform/observability/         alert rules + dashboards
#   apps/*/app.yaml                     your applications
#
# So `kubectl -n argocd get applications` lists the whole cluster, and every
# platform change is a commit.

source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

need helm; need kubectl
require_lab_context

BOOTSTRAP_DIR="${LAB_DIR}/platform/bootstrap"

# ── metrics-server (bundled with k3s) ────────────────────────
log "Verifying metrics-server (bundled with k3s)"
if kubectl -n kube-system get deployment metrics-server >/dev/null 2>&1; then
  kubectl -n kube-system rollout status deployment/metrics-server --timeout=180s >/dev/null
  ok "metrics-server available — HPA (autoscaling.enabled) will work"
else
  die "metrics-server missing. k3s ships it by default; check that --disable=metrics-server is not set in lab/k3d/cluster.yaml"
fi

# ── Argo CD — the one imperative install ─────────────────────
log "Installing Argo CD ${ARGOCD_CHART_VERSION}"
helm repo add argo https://argoproj.github.io/argo-helm >/dev/null 2>&1 || true
helm repo update argo >/dev/null
helm upgrade --install argocd argo/argo-cd \
  --version "${ARGOCD_CHART_VERSION}" \
  --namespace argocd --create-namespace \
  --values "${LAB_DIR}/platform/argocd/values.yaml" \
  --wait --timeout 10m
ok "Argo CD ready"

# ── Prometheus CRDs, first and on their own ──────────────────
# Charts that declare a ServiceMonitor need the type to exist. Creating this
# Application first and waiting for the CRD makes the ordering deterministic
# instead of relying on sync retries to converge.
log "Installing the Prometheus Operator CRDs"
kubectl apply -f "${BOOTSTRAP_DIR}/prometheus-operator-crds.yaml" >/dev/null
if kubectl wait --for=condition=established --timeout=300s \
     crd/servicemonitors.monitoring.coreos.com >/dev/null 2>&1; then
  ok "ServiceMonitor CRD established"
else
  die "the Prometheus CRDs did not install. Check: kubectl -n argocd get app prometheus-operator-crds"
fi

# ── Hand the rest of the platform to Argo CD ─────────────────
log "Applying the platform ApplicationSet"
kubectl apply -f "${BOOTSTRAP_DIR}/platform-applicationset.yaml" >/dev/null
kubectl apply -f "${BOOTSTRAP_DIR}/observability-config.yaml" >/dev/null
kubectl apply -f "${BOOTSTRAP_DIR}/postgres-config.yaml" >/dev/null
ok "platform declared — Argo CD is installing it"

# ── And the apps ─────────────────────────────────────────────
log "Applying the app ApplicationSet"
kubectl apply -f "${BOOTSTRAP_DIR}/applicationset.yaml" >/dev/null
ok "watching ${REPO_URL} (${REPO_REVISION}) for apps/*/app.yaml"

# ── Wait for the platform to converge ────────────────────────
log "Waiting for ingress-nginx (Argo CD is installing it)"
kubectl -n ingress-nginx rollout status deploy/ingress-nginx-controller --timeout=600s 2>/dev/null \
  || warn "ingress-nginx is still converging — watch: kubectl -n argocd get applications -w"

echo
log "Lab is up"
cat <<SUMMARY

  Argo CD UI     http://argocd.${LAB_DOMAIN}:${LAB_HTTP_PORT}
  Grafana        http://grafana.${LAB_DOMAIN}:${LAB_HTTP_PORT}   (admin / admin)
  passwords      make argocd-password | make grafana-password

  Everything in the cluster is an Argo CD Application:
    kubectl -n argocd get applications

  Prometheus and Grafana take a few minutes to finish pulling images.
  Watch progress with:  kubectl -n argocd get applications -w

SUMMARY

#!/usr/bin/env bash
# Install the in-cluster observability stack. Idempotent — safe to re-run.
#
#   Prometheus + Alertmanager   metrics and alerting
#   Grafana                     dashboards for both metrics and logs
#   kube-state-metrics          object state (replicas, restarts, phases)
#   node-exporter               node CPU/memory/disk
#   Loki                        log storage
#   Alloy                       log collection, DaemonSet, every pod
#
# Everything lands in the `observability` namespace inside the lab cluster,
# alongside the apps it watches.

source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

need helm; need kubectl
require_lab_context

OBS_DIR="${LAB_DIR}/platform/observability"
NS="observability"

log "Installing observability into namespace '${NS}'"

helm repo add ingress-nginx https://kubernetes.github.io/ingress-nginx >/dev/null 2>&1 || true
helm repo add prometheus-community https://prometheus-community.github.io/helm-charts >/dev/null 2>&1 || true
helm repo add grafana https://grafana.github.io/helm-charts >/dev/null 2>&1 || true
helm repo update prometheus-community grafana >/dev/null

# ── Metrics: Prometheus, Grafana, Alertmanager ───────────────
log "kube-prometheus-stack ${KUBE_PROMETHEUS_STACK_CHART_VERSION}"
helm upgrade --install kube-prometheus-stack prometheus-community/kube-prometheus-stack \
  --version "${KUBE_PROMETHEUS_STACK_CHART_VERSION}" \
  --namespace "${NS}" --create-namespace \
  --values "${OBS_DIR}/values/kube-prometheus-stack.yaml" \
  --wait --timeout 15m
ok "Prometheus, Grafana and Alertmanager ready"

# ── Scrape the ingress controller ────────────────────────────
# Now that the Prometheus CRDs exist, turn on the ingress ServiceMonitor.
# This is what gives every app request-rate, error-rate and latency metrics
# without touching the app itself.
if helm status ingress-nginx -n ingress-nginx >/dev/null 2>&1; then
  log "Enabling the ingress-nginx ServiceMonitor"
  helm upgrade ingress-nginx ingress-nginx/ingress-nginx \
    --version "${INGRESS_NGINX_CHART_VERSION}" \
    --namespace ingress-nginx \
    --values "${LAB_DIR}/platform/ingress-nginx/values.yaml" \
    --set controller.metrics.serviceMonitor.enabled=true \
    --wait --timeout 5m >/dev/null
  ok "ingress metrics are being scraped"
else
  warn "ingress-nginx is not installed; skipping its ServiceMonitor"
fi

# ── Logs: Loki ───────────────────────────────────────────────
log "Loki ${LOKI_CHART_VERSION}"
helm upgrade --install loki grafana/loki \
  --version "${LOKI_CHART_VERSION}" \
  --namespace "${NS}" \
  --values "${OBS_DIR}/values/loki.yaml" \
  --wait --timeout 10m
ok "Loki ready"

# ── Logs: Alloy collector ────────────────────────────────────
log "Alloy ${ALLOY_CHART_VERSION}"
helm upgrade --install alloy grafana/alloy \
  --version "${ALLOY_CHART_VERSION}" \
  --namespace "${NS}" \
  --values "${OBS_DIR}/values/alloy.yaml" \
  --wait --timeout 10m
ok "Alloy collecting logs from every pod"

# ── Platform-wide alert rules ────────────────────────────────
# One PrometheusRule covering every workload in the cluster, matched by
# label rather than by name — so a new app is alerted on automatically.
log "Applying platform alert rules"
kubectl apply -n "${NS}" -f "${OBS_DIR}/rules/" >/dev/null
ok "alert rules applied"

# ── Dashboards ───────────────────────────────────────────────
# ConfigMaps labelled grafana_dashboard, picked up by the Grafana sidecar.
log "Applying dashboards"
for f in "${OBS_DIR}"/dashboards/*.json; do
  [ -e "$f" ] || continue
  name="$(basename "${f}" .json)"
  kubectl create configmap "grafana-dashboard-${name}" \
    --namespace "${NS}" \
    --from-file="${name}.json=${f}" \
    --dry-run=client -o yaml \
    | kubectl label --local -f - grafana_dashboard=1 -o yaml \
    | kubectl annotate --local -f - grafana_folder="Lab" -o yaml \
    | kubectl apply -f - >/dev/null
  ok "dashboard: ${name}"
done

echo
log "Observability is up"
cat <<SUMMARY

  Grafana        http://grafana.${LAB_DOMAIN}:${LAB_HTTP_PORT}   (admin / admin)
  Prometheus     kubectl -n ${NS} port-forward svc/kube-prometheus-stack-prometheus 9090:9090
  Alertmanager   kubectl -n ${NS} port-forward svc/kube-prometheus-stack-alertmanager 9093:9093

  Logs work for every app with no configuration at all.
  App metrics need three lines in the app's values.yaml:

      metrics:
        enabled: true
        path: /metrics

SUMMARY

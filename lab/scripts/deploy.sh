#!/usr/bin/env bash
# Deploy an app straight from the working tree with helm — the fast iteration
# path for changes that are not committed yet.
#
#   make deploy APP=myapp
#
# Once the app is committed and pushed, Argo CD owns it. This script refuses to
# fight the controller: if Argo already manages the release it tells you to
# push (or to `make sync`) instead of silently causing a self-heal loop.

source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

need helm; need kubectl
require_lab_context

APP="${APP:-}"
[[ -n "${APP}" ]] || die "APP is required — e.g. make deploy APP=podinfo"

APP_DIR="${REPO_ROOT}/apps/${APP}"
[[ -f "${APP_DIR}/app.yaml"    ]] || die "apps/${APP}/app.yaml not found — run: make new-app NAME=${APP}"
[[ -f "${APP_DIR}/values.yaml" ]] || die "apps/${APP}/values.yaml not found"
[[ -d "${REPO_ROOT}/charts/generic-app" ]] || die "charts/generic-app is missing from the working tree (run: git restore .)"

NAMESPACE="$(app_field "${APP_DIR}/app.yaml" namespace)"
ENVIRONMENT="$(app_field "${APP_DIR}/app.yaml" environment)"
[[ -n "${NAMESPACE}"   ]] || die "apps/${APP}/app.yaml is missing 'namespace:'"
[[ -n "${ENVIRONMENT}" ]] || die "apps/${APP}/app.yaml is missing 'environment:'"

ENV_FILE="${REPO_ROOT}/environments/${ENVIRONMENT}.yaml"
[[ -f "${ENV_FILE}" ]] || die "environments/${ENVIRONMENT}.yaml not found"

if kubectl -n argocd get application "${APP}" >/dev/null 2>&1; then
  warn "Argo CD already manages '${APP}' with selfHeal enabled."
  warn "A direct helm release will be reverted on the next reconcile."
  warn "Push your change and run: make sync APP=${APP}"
  die  "refusing to fight the controller"
fi

log "helm upgrade --install ${APP} (namespace ${NAMESPACE}, environment ${ENVIRONMENT})"
helm upgrade --install "${APP}" "${REPO_ROOT}/charts/generic-app" \
  --namespace "${NAMESPACE}" --create-namespace \
  --values "${ENV_FILE}" \
  --values "${APP_DIR}/values.yaml" \
  --wait --timeout 5m

ok "deployed"
kubectl -n "${NAMESPACE}" get deploy,svc,ingress -l "app.kubernetes.io/instance=${APP}"

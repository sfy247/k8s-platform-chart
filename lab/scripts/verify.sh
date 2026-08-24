#!/usr/bin/env bash
# Smoke test the whole lab. Run it after recovery, after an upgrade, or any
# time you want to know whether the platform is actually working rather than
# merely running.
#
# Exits non-zero if anything fails, so it can gate a script or a pipeline.

source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

need kubectl
require_lab_context

failures=0
check() {  # check <description> <command...>
  local desc="$1"; shift
  if "$@" >/dev/null 2>&1; then
    ok "${desc}"
  else
    warn "FAILED: ${desc}"
    failures=$((failures + 1))
  fi
}

log "Cluster"
check "all nodes Ready" kubectl wait --for=condition=Ready nodes --all --timeout=60s
check "metrics-server available" kubectl -n kube-system get deployment metrics-server

log "Platform"
for ns in argocd ingress-nginx observability; do
  check "namespace ${ns} exists" kubectl get namespace "${ns}"
done
check "ingress controller ready" \
  kubectl -n ingress-nginx rollout status deploy/ingress-nginx-controller --timeout=120s
check "Argo CD server ready" kubectl -n argocd rollout status deploy/argocd-server --timeout=120s

log "Argo CD applications"
if apps="$(kubectl -n argocd get applications -o json 2>/dev/null)"; then
  unhealthy="$(printf '%s' "${apps}" | python3 -c '
import json,sys
bad=[(a["metadata"]["name"], a.get("status",{}).get("sync",{}).get("status","?"),
      a.get("status",{}).get("health",{}).get("status","?"))
     for a in json.load(sys.stdin)["items"]
     if a.get("status",{}).get("health",{}).get("status") != "Healthy"
     or a.get("status",{}).get("sync",{}).get("status") != "Synced"]
print("\n".join(f"{n} sync={s} health={h}" for n,s,h in bad))
')"
  total="$(printf '%s' "${apps}" | python3 -c 'import json,sys;print(len(json.load(sys.stdin)["items"]))')"
  if [[ -z "${unhealthy}" ]]; then
    ok "${total} application(s), all Synced and Healthy"
  else
    warn "not all applications are healthy:"
    printf '%s\n' "${unhealthy}" | sed 's/^/      /' >&2
    failures=$((failures + 1))
  fi
else
  warn "could not read Argo CD applications"
  failures=$((failures + 1))
fi

log "Pods"
notready="$(kubectl get pods -A --no-headers 2>/dev/null \
  | awk '$4 != "Running" && $4 != "Completed" {print "      " $1 "/" $2 " " $4}')"
if [[ -z "${notready}" ]]; then
  ok "every pod Running or Completed"
else
  warn "pods not running:"; printf '%s\n' "${notready}" >&2
  failures=$((failures + 1))
fi

log "HTTP endpoints"
# Every hostname the cluster actually serves — no hardcoded list to drift.
hosts="$(kubectl get ingress -A -o jsonpath='{range .items[*]}{.spec.rules[0].host}{"\n"}{end}' 2>/dev/null | sort -u)"
if [[ -z "${hosts}" ]]; then
  warn "no ingress hosts found"; failures=$((failures + 1))
fi
while read -r host; do
  [[ -n "${host}" ]] || continue
  code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 "http://${host}:${LAB_HTTP_PORT}/" || echo 000)"
  case "${code}" in
    2*|3*) ok "http://${host}:${LAB_HTTP_PORT} -> ${code}" ;;
    *)     warn "http://${host}:${LAB_HTTP_PORT} -> ${code}"; failures=$((failures + 1)) ;;
  esac
done <<< "${hosts}"

log "Observability"
check "Prometheus ready" kubectl -n observability rollout status \
  statefulset/prometheus-kube-prometheus-stack-prometheus --timeout=120s
check "Loki ready" kubectl -n observability rollout status statefulset/loki --timeout=120s
check "Alloy collecting on every node" bash -c \
  '[ "$(kubectl -n observability get ds alloy -o jsonpath="{.status.numberReady}")" = "$(kubectl get nodes --no-headers | wc -l)" ]'

echo
if [[ "${failures}" -eq 0 ]]; then
  ok "lab verified — everything is up"
else
  die "${failures} check(s) failed"
fi

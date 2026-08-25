#!/usr/bin/env bash
# Create or update a Secret for an application, reading values interactively.
#
#   make app-secret APP=trading-agent KEYS="ALPACA_API_KEY_ID ALPACA_API_SECRET_KEY"
#
# Values are typed at a prompt and go straight to the Kubernetes API. They
# are never written to a file, passed as a command argument, or recorded in
# shell history — which is what happens if you run `kubectl create secret
# --from-literal=KEY=value` by hand.
#
# Anything whose name looks like a credential is read with echo disabled.
#
# Lab pattern. In a shared environment these would come from External
# Secrets or sealed-secrets so they are reconciled from git too.

source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

need kubectl
require_lab_context

APP="${APP:-}"
KEYS="${KEYS:-}"
[[ -n "${APP}"  ]] || die "APP is required — e.g. make app-secret APP=trading-agent KEYS=\"A B\""
[[ -n "${KEYS}" ]] || die "KEYS is required — a space-separated list of variable names"

NAMESPACE="${NAMESPACE:-demo}"
SECRET="${SECRET_NAME:-${APP}-secrets}"

kubectl get namespace "${NAMESPACE}" >/dev/null 2>&1 || kubectl create namespace "${NAMESPACE}" >/dev/null

log "Creating ${SECRET} in namespace ${NAMESPACE}"
warn "values are not echoed and are not written to disk"
echo

args=()
for key in ${KEYS}; do
  case "${key}" in
    *KEY*|*SECRET*|*PASSWORD*|*TOKEN*|*CREDENTIAL*)
      read -rsp "  ${key}: " value; echo ;;
    *)
      read -rp  "  ${key}: " value ;;
  esac
  [[ -n "${value}" ]] || die "${key} cannot be empty"
  args+=("--from-literal=${key}=${value}")
  unset value
done

kubectl -n "${NAMESPACE}" create secret generic "${SECRET}" \
  "${args[@]}" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
unset args

ok "${SECRET} stored — $(kubectl -n "${NAMESPACE}" get secret "${SECRET}" -o go-template='{{len .data}}') key(s)"

cat <<NEXT

  Consume it without naming any value:

      envFrom:
        - secretRef:
            name: ${SECRET}

  Verify the keys are present (names only, no values):

      kubectl -n ${NAMESPACE} get secret ${SECRET} -o go-template='{{range \$k,\$v := .data}}{{\$k}}{{"\\n"}}{{end}}'

NEXT

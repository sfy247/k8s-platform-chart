#!/usr/bin/env bash
# Create a database role and its credentials for an application.
#
#   make db-user APP=trading [NAMESPACE=demo] [DB=trading]
#
# Generates a password, then writes two Secrets and commits neither:
#
#   data/pg-<app>-credentials     read by CloudNativePG to create the role
#   <ns>/<app>-db                 read by the app: host, port, db, user,
#                                 password, and a ready-made connection string
#
# Idempotent: an existing password is reused rather than rotated, because
# rotating one side without the other locks the app out of its own database.
#
# Lab pattern. In a shared environment these Secrets would come from
# External Secrets or sealed-secrets so they are reconciled from git too.

source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

need kubectl
require_lab_context

APP="${APP:-}"
[[ -n "${APP}" ]] || die "APP is required — e.g. make db-user APP=trading"
[[ "${APP}" =~ ^[a-z0-9]([-a-z0-9]*[a-z0-9])?$ ]] || die "APP must be a DNS label"

NAMESPACE="${NAMESPACE:-demo}"
DB="${DB:-${APP//-/_}}"
ROLE="${DB}_app"
CLUSTER_SECRET="pg-${APP}-credentials"
APP_SECRET="${APP}-db"
PG_HOST="lab-pg-rw.data.svc.cluster.local"
PG_PORT="5432"

kubectl get namespace data >/dev/null 2>&1 || die "namespace 'data' not found — is the postgres platform app synced?"

# Reuse an existing password so the two Secrets never drift apart.
if password="$(kubectl -n data get secret "${CLUSTER_SECRET}" -o jsonpath='{.data.password}' 2>/dev/null | base64 -d)" \
   && [[ -n "${password}" ]]; then
  ok "reusing the existing password for ${ROLE}"
else
  password="$(head -c 32 /dev/urandom | base64 | tr -d '/+=' | head -c 32)"
  log "generated a new password for ${ROLE}"
fi

log "Writing ${CLUSTER_SECRET} in namespace data (read by CloudNativePG)"
kubectl -n data create secret generic "${CLUSTER_SECRET}" \
  --type=kubernetes.io/basic-auth \
  --from-literal=username="${ROLE}" \
  --from-literal=password="${password}" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null

# Without this label the operator's filtered cache never sees the Secret and
# the role sits in pending-reconciliation forever, reporting
# "failed to get password secret ... not found" while the Secret plainly
# exists. Costly to debug; one line to prevent.
kubectl -n data label secret "${CLUSTER_SECRET}" cnpg.io/reload=true --overwrite >/dev/null
ok "role credentials stored"

log "Writing ${APP_SECRET} in namespace ${NAMESPACE} (read by the app)"
kubectl get namespace "${NAMESPACE}" >/dev/null 2>&1 || kubectl create namespace "${NAMESPACE}" >/dev/null
kubectl -n "${NAMESPACE}" create secret generic "${APP_SECRET}" \
  --from-literal=POSTGRES_HOST="${PG_HOST}" \
  --from-literal=POSTGRES_PORT="${PG_PORT}" \
  --from-literal=POSTGRES_DB="${DB}" \
  --from-literal=POSTGRES_USER="${ROLE}" \
  --from-literal=POSTGRES_PASSWORD="${password}" \
  --from-literal=DATABASE_URL="postgresql://${ROLE}:${password}@${PG_HOST}:${PG_PORT}/${DB}" \
  --from-literal=ConnectionStrings__Default="Host=${PG_HOST};Port=${PG_PORT};Database=${DB};Username=${ROLE};Password=${password}" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null
ok "app credentials stored"

cat <<NEXT

  Consume them in apps/${APP}/values.yaml without naming any secret value:

      envFrom:
        - secretRef:
            name: ${APP_SECRET}

  Still required in git (reviewable, no secrets):
    - a Database in lab/platform/postgres/manifests/databases.yaml
    - a managed role in lab/platform/postgres/manifests/cluster.yaml

NEXT

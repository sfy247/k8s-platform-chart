#!/usr/bin/env bash
# Create the k3d lab cluster from lab/k3d/cluster.yaml. Idempotent.

source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

need k3d; need kubectl

# k3d publishes these on the host; a collision fails cluster creation halfway
# through and rolls back. Check first and say exactly what is in the way.
check_port() {
  local port="$1" label="$2"
  if ss -ltn 2>/dev/null | awk -v p=":${port}$" '$4 ~ p {found=1} END{exit !found}'; then
    local holder
    holder="$(docker ps --format '{{.Names}} {{.Ports}}' | grep ":${port}->" | cut -d' ' -f1 || true)"
    die "host port ${port} (${label}) is already in use${holder:+ by container '${holder}'}.
       Free it, or change the port in lab/k3d/cluster.yaml and LAB_${label}_PORT in lab/versions.env."
  fi
}

if ! cluster_exists; then
  check_port "${LAB_HTTP_PORT}" "HTTP"
  check_port "${LAB_HTTPS_PORT}" "HTTPS"
fi

if cluster_exists; then
  ok "cluster '${CLUSTER_NAME}' already exists"
else
  log "Creating k3d cluster '${CLUSTER_NAME}' (1 server + 2 agents)"
  k3d cluster create --config "${LAB_DIR}/k3d/cluster.yaml"
fi

kubectl config use-context "${KUBE_CONTEXT}" >/dev/null
log "Waiting for nodes to become Ready"
kubectl wait --for=condition=Ready nodes --all --timeout=180s >/dev/null
kubectl get nodes -o wide

ok "cluster ready — context ${KUBE_CONTEXT}"
echo
echo "  HTTP  ingress: http://<host>.${LAB_DOMAIN}:${LAB_HTTP_PORT}"
echo "  HTTPS ingress: https://<host>.${LAB_DOMAIN}:${LAB_HTTPS_PORT}"
echo "  Next: make bootstrap"

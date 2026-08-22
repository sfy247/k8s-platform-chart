#!/usr/bin/env bash
# Delete the k3d lab cluster. Only ever touches the cluster named in
# lab/k3d/cluster.yaml — it cannot reach any other context.

source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

need k3d

if ! cluster_exists; then
  ok "cluster '${CLUSTER_NAME}' does not exist — nothing to do"
  exit 0
fi

log "Deleting k3d cluster '${CLUSTER_NAME}' (all workloads and local volumes are destroyed)"
k3d cluster delete "${CLUSTER_NAME}"
ok "cluster '${CLUSTER_NAME}' deleted"

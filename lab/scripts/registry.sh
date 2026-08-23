#!/usr/bin/env bash
# Create the local image registry if it does not exist.
#
# Deliberately standalone rather than part of the cluster: `k3d cluster
# delete` removes clusters, not registries, so images pushed here outlive
# every rebuild. That turns disaster recovery from "rebuild every image from
# source" into "the cluster pulls what is already there".
#
# Storage is a named Docker volume, so even recreating the registry container
# keeps the images.

source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

need k3d; need docker

if k3d registry list --output json 2>/dev/null | grep -q "\"k3d-${REGISTRY_NAME}\""; then
  running="$(docker inspect -f '{{.State.Running}}' "k3d-${REGISTRY_NAME}" 2>/dev/null || echo false)"
  if [[ "${running}" != "true" ]]; then
    log "starting existing registry k3d-${REGISTRY_NAME}"
    docker start "k3d-${REGISTRY_NAME}" >/dev/null
  fi
  ok "registry k3d-${REGISTRY_NAME} available on ${REGISTRY_HOST}"
else
  log "Creating registry ${REGISTRY_NAME} on port ${REGISTRY_PORT}"
  docker volume create "${REGISTRY_VOLUME}" >/dev/null
  k3d registry create "${REGISTRY_NAME}" \
    --port "0.0.0.0:${REGISTRY_PORT}" \
    --volume "${REGISTRY_VOLUME}:/var/lib/registry"
  ok "registry created — images survive cluster deletion"
fi

# Prove it answers before anything tries to push.
for _ in $(seq 1 30); do
  if curl -sf "http://${REGISTRY_HOST}/v2/" >/dev/null 2>&1; then
    ok "registry responding at http://${REGISTRY_HOST}/v2/"
    exit 0
  fi
  sleep 1
done
die "registry at ${REGISTRY_HOST} did not respond — check: docker logs k3d-${REGISTRY_NAME}"

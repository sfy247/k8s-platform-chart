#!/usr/bin/env bash
# Rebuild and import every locally-built image the apps depend on.
#
# This is the step that makes disaster recovery actually work. The cluster
# and the platform rebuild themselves from git, but images built on this
# machine live only in the cluster's image store — destroy the cluster and
# they are gone. Nothing in git can bring them back, so they are rebuilt.
#
# The source of truth is each app's values.yaml: if image.repository matches
# a directory under services/, it is a local build and gets rebuilt at the
# exact tag the app asks for.
#
#   make images                    rebuild and import everything
#   make images APP=hello-python   just one

source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

need docker; need k3d

only="${APP:-}"
built=0
skipped=0

log "Rebuilding locally-built images"

for dir in "${REPO_ROOT}"/apps/*/; do
  app="$(basename "${dir}")"
  values="${dir}values.yaml"
  [[ -f "${values}" ]] || continue
  [[ -z "${only}" || "${only}" == "${app}" ]] || continue

  repo="$(sed -n 's/^  repository:[[:space:]]*//p' "${values}" | head -1 | tr -d '"' | tr -d "'")"
  tag="$(sed -n 's/^  tag:[[:space:]]*//p' "${values}" | head -1 | tr -d '"' | tr -d "'")"
  [[ -n "${repo}" && -n "${tag}" ]] || continue

  src="${REPO_ROOT}/services/${repo}"
  if [[ ! -d "${src}" ]]; then
    skipped=$((skipped + 1))
    continue      # pulled from a registry; the cluster fetches it itself
  fi

  log "building ${repo}:${tag} from services/${repo}"
  docker build -q -t "${repo}:${tag}" "${src}" >/dev/null
  k3d image import "${repo}:${tag}" --cluster "${CLUSTER_NAME}" >/dev/null 2>&1
  ok "${repo}:${tag} built and imported"
  built=$((built + 1))
done

ok "${built} image(s) rebuilt, ${skipped} pulled from registries by the cluster"

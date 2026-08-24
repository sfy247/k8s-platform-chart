#!/usr/bin/env bash
# Build locally-built images and push them to the lab registry.
#
# The registry outlives the cluster, so on recovery this is usually a no-op:
# the images are already there and the cluster simply pulls them. Only a
# genuinely missing image is rebuilt from source.
#
#   make images                      ensure every app's image is available
#   make images APP=hello-python     just one
#   make images FORCE=1              rebuild even if the registry has it
#
# The source of truth is each app's values.yaml: if image.repository matches
# a directory under services/, it is a local build, and it is built at
# exactly the tag the app asks for.

source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

need docker
"${LAB_DIR}/scripts/registry.sh" >/dev/null || die "the lab registry is not available"

only="${APP:-}"
force="${FORCE:-0}"
built=0; reused=0; skipped=0

in_registry() {  # in_registry <repo> <tag>
  # BuildKit pushes an OCI image INDEX, not a plain manifest, so the index
  # media types have to be in Accept or the registry answers 404 for an
  # image that is plainly there.
  curl -sf "http://${REGISTRY_HOST}/v2/$1/manifests/$2" \
    -H "Accept: application/vnd.oci.image.index.v1+json" \
    -H "Accept: application/vnd.docker.distribution.manifest.list.v2+json" \
    -H "Accept: application/vnd.oci.image.manifest.v1+json" \
    -H "Accept: application/vnd.docker.distribution.manifest.v2+json" >/dev/null 2>&1
}

# A cluster created before the registry existed has no mirror configured and
# cannot pull localhost:5111/... . Importing the image directly means
# pullPolicy: IfNotPresent finds it locally either way, so the registry can
# be adopted without recreating a running cluster. Harmless once every
# cluster is created from the current lab/k3d/cluster.yaml.
import_to_cluster() {  # import_to_cluster <repo> <tag>
  cluster_exists || return 0
  local ref="${REGISTRY_HOST}/$1:$2"
  docker image inspect "${ref}" >/dev/null 2>&1 || docker pull -q "${ref}" >/dev/null 2>&1 || return 0
  k3d image import "${ref}" --cluster "${CLUSTER_NAME}" >/dev/null 2>&1 \
    && ok "  imported into the running cluster" || true
}

log "Ensuring images are in ${REGISTRY_HOST}"

for dir in "${REPO_ROOT}"/apps/*/; do
  app="$(basename "${dir}")"
  values="${dir}values.yaml"
  [[ -f "${values}" ]] || continue
  [[ -z "${only}" || "${only}" == "${app}" ]] || continue

  repo="$(sed -n 's/^  repository:[[:space:]]*//p' "${values}" | head -1 | tr -d "\"'")"
  tag="$(sed -n 's/^  tag:[[:space:]]*//p' "${values}" | head -1 | tr -d "\"'")"
  [[ -n "${repo}" && -n "${tag}" ]] || continue

  src="${REPO_ROOT}/services/${repo}"
  if [[ ! -d "${src}" ]]; then
    skipped=$((skipped + 1))
    continue      # pulled from a public registry; the cluster fetches it
  fi

  if [[ "${force}" != "1" ]] && in_registry "${repo}" "${tag}"; then
    ok "${repo}:${tag} already in the registry"
    reused=$((reused + 1))
    import_to_cluster "${repo}" "${tag}"
    continue
  fi

  log "building ${repo}:${tag} from services/${repo}"
  docker build -q -t "${REGISTRY_HOST}/${repo}:${tag}" "${src}" >/dev/null
  docker push -q "${REGISTRY_HOST}/${repo}:${tag}" >/dev/null
  ok "${repo}:${tag} built and pushed"
  built=$((built + 1))
  import_to_cluster "${repo}" "${tag}"
done

ok "${built} built, ${reused} already present, ${skipped} pulled from public registries"

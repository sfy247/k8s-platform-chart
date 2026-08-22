#!/usr/bin/env bash
# Install/verify the pinned lab toolchain. No sudo, no system packages —
# everything lands in $LAB_BIN_DIR (default ~/.local/bin) and is checksum
# verified where upstream publishes checksums.

source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

log "Preflight — toolchain into ${LAB_BIN_DIR}"
mkdir -p "${LAB_BIN_DIR}"

case ":${PATH}:" in
  *":${LAB_BIN_DIR}:"*) ;;
  *) warn "${LAB_BIN_DIR} is not on your PATH. Add it to your shell profile:"
     warn "    export PATH=\"${LAB_BIN_DIR}:\$PATH\"" ;;
esac

# ── Docker ───────────────────────────────────────────────────
command -v docker >/dev/null 2>&1 || die "docker not found — k3d runs Kubernetes nodes as containers."
docker info >/dev/null 2>&1 || die "cannot talk to the Docker daemon (is it running, and is your user in the 'docker' group?)"
ok "docker $(docker version --format '{{.Server.Version}}' 2>/dev/null)"

have_version() {  # have_version <binary> <version-string>
  command -v "$1" >/dev/null 2>&1 && "$1" version --client 2>/dev/null | grep -q "$2"
}

verify_sha() {  # verify_sha <file> <expected-sha256>
  local file="$1" expected="$2" actual
  actual="$(sha256sum "${file}" | cut -d' ' -f1)"
  [[ "${actual}" == "${expected}" ]] || die "checksum mismatch for ${file}: expected ${expected}, got ${actual}"
}

# ── kubectl ──────────────────────────────────────────────────
if command -v kubectl >/dev/null 2>&1 && kubectl version --client 2>/dev/null | grep -q "${KUBECTL_VERSION}"; then
  ok "kubectl ${KUBECTL_VERSION}"
else
  log "Installing kubectl ${KUBECTL_VERSION}"
  curl -fsSL -o "${TMP}/kubectl" "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/amd64/kubectl"
  curl -fsSL -o "${TMP}/kubectl.sha256" "https://dl.k8s.io/release/${KUBECTL_VERSION}/bin/linux/amd64/kubectl.sha256"
  verify_sha "${TMP}/kubectl" "$(cat "${TMP}/kubectl.sha256")"
  install -m 0755 "${TMP}/kubectl" "${LAB_BIN_DIR}/kubectl"
  ok "kubectl ${KUBECTL_VERSION} installed"
fi

# ── helm ─────────────────────────────────────────────────────
if command -v helm >/dev/null 2>&1 && helm version --short 2>/dev/null | grep -q "${HELM_VERSION}"; then
  ok "helm ${HELM_VERSION}"
else
  log "Installing helm ${HELM_VERSION}"
  curl -fsSL -o "${TMP}/helm.tgz" "https://get.helm.sh/helm-${HELM_VERSION}-linux-amd64.tar.gz"
  curl -fsSL -o "${TMP}/helm.sha256" "https://get.helm.sh/helm-${HELM_VERSION}-linux-amd64.tar.gz.sha256sum"
  verify_sha "${TMP}/helm.tgz" "$(cut -d' ' -f1 "${TMP}/helm.sha256")"
  tar -xzf "${TMP}/helm.tgz" -C "${TMP}"
  install -m 0755 "${TMP}/linux-amd64/helm" "${LAB_BIN_DIR}/helm"
  ok "helm ${HELM_VERSION} installed"
fi

# ── k3d ──────────────────────────────────────────────────────
if command -v k3d >/dev/null 2>&1 && k3d version 2>/dev/null | grep -q "k3d version ${K3D_VERSION}"; then
  ok "k3d ${K3D_VERSION}"
else
  log "Installing k3d ${K3D_VERSION}"
  base="https://github.com/k3d-io/k3d/releases/download/${K3D_VERSION}"
  curl -fsSL -o "${TMP}/k3d" "${base}/k3d-linux-amd64"
  if curl -fsSL -o "${TMP}/k3d_checksums.txt" "${base}/checksums.txt" 2>/dev/null; then
    expected="$(awk '/k3d-linux-amd64$/{print $1; exit}' "${TMP}/k3d_checksums.txt")"
    [[ -n "${expected}" ]] && verify_sha "${TMP}/k3d" "${expected}" || warn "no checksum entry for k3d-linux-amd64; skipping verification"
  else
    warn "k3d checksums unavailable; skipping verification"
  fi
  install -m 0755 "${TMP}/k3d" "${LAB_BIN_DIR}/k3d"
  ok "k3d ${K3D_VERSION} installed"
fi

log "Preflight complete"

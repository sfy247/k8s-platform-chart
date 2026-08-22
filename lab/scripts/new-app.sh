#!/usr/bin/env bash
# Scaffold a new app: apps/<name>/{app.yaml,values.yaml}
#
#   make new-app NAME=myapp [NAMESPACE=demo] [APP_ENV=local] [PORT=8080] [IMAGE=...] [TAG=...]

source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

NAME="${NAME:-}"
[[ -n "${NAME}" ]] || die "NAME is required — e.g. make new-app NAME=myapp"
[[ "${NAME}" =~ ^[a-z0-9]([-a-z0-9]*[a-z0-9])?$ ]] \
  || die "NAME must be a valid DNS label (lowercase alphanumerics and '-')"

NAMESPACE="${NAMESPACE:-demo}"
ENVIRONMENT="${APP_ENV:-local}"
PORT="${PORT:-8080}"
IMAGE="${IMAGE:-ghcr.io/sfy247/${NAME}}"
TAG="${TAG:-latest}"

[[ -f "${REPO_ROOT}/environments/${ENVIRONMENT}.yaml" ]] \
  || die "no environments/${ENVIRONMENT}.yaml — pick one of: $(cd "${REPO_ROOT}/environments" && ls *.yaml | sed 's/.yaml//' | tr '\n' ' ')"

APP_DIR="${REPO_ROOT}/apps/${NAME}"
[[ -e "${APP_DIR}" ]] && die "apps/${NAME} already exists — edit it instead of scaffolding over it"

mkdir -p "${APP_DIR}"
for f in app values; do
  sed -e "s|__NAME__|${NAME}|g" \
      -e "s|__NAMESPACE__|${NAMESPACE}|g" \
      -e "s|__ENVIRONMENT__|${ENVIRONMENT}|g" \
      -e "s|__PORT__|${PORT}|g" \
      -e "s|__IMAGE__|${IMAGE}|g" \
      -e "s|__TAG__|${TAG}|g" \
      "${LAB_DIR}/scaffold/${f}.yaml.tmpl" > "${APP_DIR}/${f}.yaml"
done

ok "created apps/${NAME}/app.yaml and apps/${NAME}/values.yaml"
cat <<NEXT

  1. edit apps/${NAME}/values.yaml   (image, port, probe paths)
  2. try it now:      make deploy APP=${NAME}
  3. make it real:    git add apps/${NAME} && git commit && git push
                      Argo CD picks it up within ~60s (make sync APP=${NAME} to force)

  URL once running:   http://${NAME}.${LAB_DOMAIN}:${LAB_HTTP_PORT}

NEXT

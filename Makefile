# ─────────────────────────────────────────────────────────────────────────
# Local Kubernetes lab — k3d + ingress-nginx + Argo CD + charts/generic-app
#
#   make lab-up                  create the cluster and bootstrap the platform
#   make new-app NAME=myapp      scaffold apps/myapp/
#   make deploy APP=myapp        deploy from the working tree (pre-commit)
#   make lab-down                destroy the cluster
# ─────────────────────────────────────────────────────────────────────────
SHELL := /usr/bin/env bash
.DEFAULT_GOAL := help

SCRIPTS := lab/scripts

# Pass command-line variables through to the scripts explicitly.
export NAME APP NAMESPACE APP_ENV PORT IMAGE TAG
CHART   := charts/generic-app

.PHONY: help preflight cluster-up cluster-down lab-up lab-down bootstrap \
        new-app deploy uninstall image-import status sync argocd-password \
        grafana-password platform recover images verify \
        logs lint template validate

help: ## Show available targets
	@awk 'BEGIN{FS=":.*?## "} /^[a-zA-Z_-]+:.*?## /{printf "  \033[36m%-18s\033[0m %s\n",$$1,$$2}' $(MAKEFILE_LIST)

# ── Cluster lifecycle ────────────────────────────────────────
preflight: ## Install/verify pinned kubectl, helm, k3d (no sudo, ~/.local/bin)
	@$(SCRIPTS)/preflight.sh

cluster-up: preflight ## Create the k3d lab cluster
	@$(SCRIPTS)/cluster-up.sh

bootstrap: ## Install Argo CD, then let it install the platform and apps
	@$(SCRIPTS)/bootstrap.sh

lab-up: cluster-up bootstrap ## Create the cluster and bootstrap the platform

lab-down: ## Delete the lab cluster (destroys all workloads and local volumes)
	@$(SCRIPTS)/cluster-down.sh

lab-reset: lab-down lab-up ## Recreate the lab from scratch

# ── Disaster recovery ────────────────────────────────────────
recover: ## Rebuild the whole lab and verify it. FRESH=1 destroys the cluster first
	@$(SCRIPTS)/recover.sh

images: ## Rebuild and import every locally-built image: make images [APP=myapp]
	@$(SCRIPTS)/images.sh

verify: ## Smoke test the whole lab — nodes, platform, apps, endpoints
	@$(SCRIPTS)/verify.sh

# ── App workflow ─────────────────────────────────────────────
new-app: ## Scaffold an app: make new-app NAME=myapp [NAMESPACE=demo] [APP_ENV=local] [PORT=8080] [IMAGE=...] [TAG=...]
	@$(SCRIPTS)/new-app.sh

deploy: ## Deploy from the working tree (pre-commit): make deploy APP=myapp
	@$(SCRIPTS)/deploy.sh

uninstall: ## Remove a helm-deployed app: make uninstall APP=myapp NAMESPACE=demo
	@test -n "$(APP)" || { echo "APP is required"; exit 1; }
	helm uninstall $(APP) --namespace $(or $(NAMESPACE),demo)

image-import: ## Load a locally built image into the cluster: make image-import IMAGE=myapp:dev
	@test -n "$(IMAGE)" || { echo "IMAGE is required"; exit 1; }
	k3d image import $(IMAGE) --cluster lab

# ── Argo CD ──────────────────────────────────────────────────
sync: ## Force an Argo CD sync: make sync APP=myapp
	@test -n "$(APP)" || { echo "APP is required"; exit 1; }
	kubectl -n argocd patch application $(APP) --type merge \
	  -p '{"operation":{"initiatedBy":{"username":"make"},"sync":{"revision":"HEAD"}}}'

grafana-password: ## Print the Grafana admin password
	@kubectl -n observability get secret kube-prometheus-stack-grafana \
	  -o jsonpath='{.data.admin-password}' | base64 -d; echo

argocd-password: ## Print the Argo CD admin password
	@kubectl -n argocd get secret argocd-initial-admin-secret \
	  -o jsonpath='{.data.password}' | base64 -d; echo

platform: ## Show the platform Applications Argo CD manages
	@kubectl -n argocd get applications -l lab.sfy247.io/layer=platform

status: ## Show cluster, platform and application state
	@kubectl get nodes
	@echo; if [ -n "$$(kubectl -n argocd get applications --no-headers 2>/dev/null)" ]; then \
	  kubectl -n argocd get applications; \
	else echo "no Argo CD applications yet (apps appear after they are pushed to the remote)"; fi
	@echo; kubectl get pods -A -o wide --no-headers | awk '{printf "%-16s %-52s %s\n",$$1,$$2,$$4}' | sort
	@echo; kubectl get ingress -A

logs: ## Tail an app's logs: make logs APP=myapp [NAMESPACE=demo]
	@test -n "$(APP)" || { echo "APP is required"; exit 1; }
	kubectl -n $(or $(NAMESPACE),demo) logs -l app.kubernetes.io/instance=$(APP) --tail=100 -f

# ── Chart validation (same checks CI runs) ───────────────────
lint: ## helm lint the chart
	helm lint $(CHART)

template: ## Render the chart for every app in apps/
	@for app in apps/*/; do \
	  name=$$(basename $$app); \
	  env=$$(sed -n 's/^environment:[[:space:]]*//p' $$app/app.yaml | head -1); \
	  echo "--- $$name ($$env) ---"; \
	  helm template $$name $(CHART) -f environments/$$env.yaml -f $$app/values.yaml >/dev/null \
	    && echo "ok" || exit 1; \
	done

validate: lint template ## Run all local chart validation

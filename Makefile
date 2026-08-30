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
export NAME APP NAMESPACE APP_ENV PORT IMAGE TAG DB FORCE KEYS SECRET_NAME
CHART   := charts/generic-app

.PHONY: help preflight cluster-up cluster-down lab-up lab-down bootstrap \
        new-app deploy uninstall image-import status sync argocd-password \
        grafana-password platform recover images verify registry images-list \
        db-user db-shell db-status app-secret \
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

images: ## Build+push local images to the lab registry: make images [APP=x] [FORCE=1]
	@$(SCRIPTS)/images.sh

app-secret: ## Create a Secret from prompted values: make app-secret APP=x KEYS="A B"
	@$(SCRIPTS)/app-secret.sh

db-user: ## Create a DB role + credentials for an app: make db-user APP=trading [NAMESPACE=demo] [DB=trading]
	@$(SCRIPTS)/db-user.sh

ca-export: ## Write the lab root CA to lab-root-ca.crt for browser/OS trust
	@kubectl -n cert-manager get secret lab-root-ca -o jsonpath='{.data.tls\.crt}' \
	  | base64 -d > lab-root-ca.crt
	@echo "  wrote lab-root-ca.crt"
	@echo
	@echo "  Trust it system-wide (asks for your password):"
	@echo "    sudo cp lab-root-ca.crt /usr/local/share/ca-certificates/sfy247-lab.crt"
	@echo "    sudo update-ca-certificates"
	@echo
	@echo "  Firefox keeps its own store:"
	@echo "    Settings -> Privacy & Security -> Certificates -> View Certificates"
	@echo "    -> Authorities -> Import -> lab-root-ca.crt -> trust for websites"
	@echo
	@echo "  Restart the browser afterwards."

ca-check: ## Show which certificate a lab host is serving
	@echo | openssl s_client -connect $(or $(HOST),grafana.localtest.me):8543 \
	  -servername $(or $(HOST),grafana.localtest.me) 2>/dev/null \
	  | openssl x509 -noout -subject -issuer -dates

db-shell: ## Open psql on the shared cluster: make db-shell [DB=lab]
	kubectl -n data exec -it lab-pg-1 -- psql -U postgres -d $(or $(DB),lab)

db-status: ## Show the PostgreSQL cluster and its databases
	@kubectl -n data get cluster,database,pods 2>/dev/null || echo "postgres not installed yet"

registry: ## Create/start the local image registry (survives cluster deletion)
	@$(SCRIPTS)/registry.sh

images-list: ## List images stored in the lab registry
	@curl -s http://localhost:5111/v2/_catalog | python3 -c "import sys,json;[print(' ',r) for r in json.load(sys.stdin)['repositories']]" 2>/dev/null || echo "registry not running — make registry"
	@for r in $$(curl -s http://localhost:5111/v2/_catalog 2>/dev/null | python3 -c "import sys,json;print(' '.join(json.load(sys.stdin)['repositories']))" 2>/dev/null); do \
	  printf "    %-20s " "$$r"; curl -s http://localhost:5111/v2/$$r/tags/list | python3 -c "import sys,json;print(json.load(sys.stdin).get('tags'))"; done

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

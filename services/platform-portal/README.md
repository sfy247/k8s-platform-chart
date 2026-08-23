# platform-portal

One page listing every application in the cluster, with live health.

Apps are discovered from **Kubernetes Ingress objects** — nothing is
hardcoded and there is no registry to maintain. Deploy an app with an
Ingress and it appears on the next refresh.

## Endpoints

| Path | Purpose |
|---|---|
| `/` | the portal page (auto-refreshes) |
| `/api/apps` | the same data as JSON |
| `/healthz` | liveness — process is up, independent of the cluster |
| `/readyz` | readiness — 503 until the first discovery succeeds |
| `/metrics` | Prometheus: apps discovered, apps by status, refresh failures |

## Configuration

| Variable | Default | Notes |
|---|---|---|
| `PORT` | `8080` | |
| `PORTAL_TITLE` | `Platform Portal` | heading |
| `WATCH_NAMESPACES` | *(all)* | comma-separated; empty means every namespace RBAC allows |
| `PLATFORM_NAMESPACES` | `argocd,observability,ingress-nginx,kube-system` | shown in a separate section |
| `URL_SUFFIX` | *(empty)* | appended to hostnames. `:8090` for the k3d lab, empty on EKS |
| `REFRESH_SECONDS` | `30` | discovery + probe interval |
| `PROBE_TIMEOUT_SECONDS` | `3.0` | per-app health probe timeout |
| `DEFAULT_HEALTH_PATH` | `/healthz` | override per app with an annotation |

## Per-app annotations

Set on the app's Ingress (`ingress.annotations` in its values.yaml):

```yaml
portal.sfy247.io/health-path: /readyz      # default /healthz
portal.sfy247.io/description: What it does
portal.sfy247.io/icon: "🐍"
portal.sfy247.io/hide: "true"              # keep it off the portal
```

## RBAC

Needs read access to Ingresses, and a mounted ServiceAccount token:

```yaml
serviceAccount:
  automountToken: true
rbac:
  enabled: true
  scope: ClusterRole
  rules:
    - apiGroups: ["networking.k8s.io"]
      resources: ["ingresses"]
      verbs: ["get", "list"]
```

Read-only, and only Ingresses — it cannot read Secrets, ConfigMaps or pods.

## Local development

```bash
uv sync
uv run pytest                 # 14 tests, no cluster needed
```

Running outside a cluster starts the server but discovery is disabled: there
is no ServiceAccount token to authenticate with, and `/readyz` stays 503.

## Deploy

```bash
docker build -t platform-portal:0.1.0 .
make image-import IMAGE=platform-portal:0.1.0      # from the repo root
```

## On EKS

The image is cluster-agnostic. What changes is values, not code:

| | Lab (k3d) | EKS |
|---|---|---|
| Image | `k3d image import` | push to ECR |
| `URL_SUFFIX` | `:8090` | empty |
| `ingress.className` | `nginx` | `alb` or your controller |
| `ingress.tls` | false | true, via cert-manager or ACM |
| Architecture | amd64 | amd64 or arm64 for Graviton |

No IRSA is needed — the portal talks to the Kubernetes API, not to AWS.
If NetworkPolicies are enforced, allow egress from the portal to the apps it
probes and to the API server.

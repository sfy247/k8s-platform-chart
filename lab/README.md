# Local Kubernetes lab

A disposable k3d cluster with just enough platform on it that deploying an app
is a two-file change. Built for `charts/generic-app`.

```
  make lab-up
        │
        ▼
  k3d cluster "lab"            1 server + 2 agents, k3s v1.36.0
        │
        ├── metrics-server     bundled with k3s (HPA depends on it)
        ├── ingress-nginx      LoadBalancer -> ServiceLB -> host :8090/:8543
        ├── Argo CD            reconciles this repo
        └── observability      Prometheus + Grafana + Alertmanager
                               Loki + Alloy (logs from every pod)
                 │
                 └── ApplicationSet "lab-apps"
                          scans apps/*/app.yaml
                          renders charts/generic-app
                          with environments/<env>.yaml + apps/<name>/values.yaml
```

## Prerequisites

Docker, and nothing else. `make preflight` installs pinned `kubectl`, `helm`
and `k3d` into `~/.local/bin` (no sudo, checksum verified). Versions live in
[`versions.env`](versions.env).

## Bring it up

```bash
make lab-up            # ~3-5 minutes on first run (image pulls)
make status
make argocd-password   # login: admin
```

| Thing | Where |
|---|---|
| Argo CD UI | http://argocd.localtest.me:8090 |
| Grafana | http://grafana.localtest.me:8090 (admin / admin) |
| Any app | `http://<app>.localtest.me:8090` |
| Cluster context | `k3d-lab` |

`*.localtest.me` is public wildcard DNS pointing at loopback — no `/etc/hosts`
edits, no dnsmasq.

## Deploy an app

```bash
make new-app NAME=myapp PORT=3000 IMAGE=ghcr.io/sfy247/myapp
$EDITOR apps/myapp/values.yaml
make deploy APP=myapp                    # working-tree deploy, no commit needed
git add apps/myapp && git commit -m "feat: add myapp" && git push
```

Once the app is pushed and Argo CD adopts it, drop any release you created by
hand so there is exactly one owner:

```bash
helm uninstall myapp -n demo      # only if you ran `make deploy` first
```

Argo CD reads from the **git remote**, not your working tree. Before a push,
`make deploy` runs helm directly; after a push, Argo CD takes ownership and
`make deploy` refuses to fight it (`selfHeal` would just revert you).

### Images you build locally

There is no registry in the lab, so push the image into the cluster's
containerd instead:

```bash
docker build -t myapp:dev .
make image-import IMAGE=myapp:dev
# apps/myapp/values.yaml -> image.repository: myapp, image.tag: dev
```

`environments/local.yaml` sets `pullPolicy: IfNotPresent` so the imported image
is used instead of being fetched from a registry that does not have it.

## Tear it down

```bash
make lab-down     # deletes the cluster, its volumes, everything on it
make lab-reset    # down + up
```

Nothing outside the `lab` cluster and `~/.local/bin` is touched.

## Observability

Runs in-cluster in the `observability` namespace, watching every app.

| What a new app gets | Configuration needed |
|---|---|
| Logs in Grafana, searchable by app | **none** — Alloy collects every pod's stdout |
| CPU, memory, restarts, replica health | **none** — kube-state-metrics and cAdvisor |
| Request rate, error rate, p95 latency | **none** — from the ingress controller |
| Alerts on 5xx, latency, crash loops, OOMKills | **none** — platform rules match by label |
| Its own application metrics | 3 lines in `apps/<name>/values.yaml` |

```yaml
metrics:
  enabled: true
  path: /metrics
```

That renders a ServiceMonitor. Prometheus is configured with
`serviceMonitorSelectorNilUsesHelmValues: false`, so it discovers the new
target on its own — enabling metrics never requires a platform change.

**One dashboard serves every app.** Grafana → *Lab* → **Application Overview**,
then pick a namespace and app from the dropdowns. Traffic, workload and logs
on one page. There is no per-app dashboard to write.

```bash
make observability        # install or upgrade the stack on its own
make grafana-password
kubectl -n observability get pods
```

Costs roughly 3 GB of RAM. Skip it with `LAB_SKIP_OBSERVABILITY=1 make lab-up`.

### Useful queries

```logql
{app="hello-python"}                          # all logs for an app
{app="hello-python", severity="ERROR"}        # JSON severity was parsed out
{namespace="demo"} |= "timeout"               # free-text across a namespace
```

```promql
sum(rate(nginx_ingress_controller_requests{ingress="hello-python"}[5m]))
sum(rate(hello_python_requests_total[5m])) by (status)
```

## What is deliberately not here

| Not installed | Why, and what to do instead |
|---|---|
| Local registry | `k3d image import` covers local builds. Add `k3d registry create` if you start pushing between clusters. |
| cert-manager | No trusted local CA, so `ingress.tls: true` falls back to nginx's self-signed cert. Add it when you need to exercise TLS paths. |
| Tempo / tracing | Metrics and logs answer "what broke". Traces answer "where in a request chain" — worth adding once there are services calling each other. The OTel SDK would go in the app; Tempo is another `helm upgrade --install` here. |

Each is a `helm upgrade --install` in `lab/scripts/bootstrap.sh` plus a values
file under `lab/platform/` — the same shape as the two add-ons already there.

## Troubleshooting

| Symptom | Where to look |
|---|---|
| App URL returns 404 | `kubectl get ingress -A` — is the host right, and does `ingress.enabled: true`? |
| App URL refuses connection | `kubectl -n ingress-nginx get svc` — the controller Service needs an EXTERNAL-IP from ServiceLB |
| Pod stuck `ContainerCreating` | `kubectl -n <ns> describe pod <pod>` — usually an image that was never imported |
| Pod never Ready | Probe paths. The chart defaults to `/health`; podinfo uses `/healthz` + `/readyz` |
| Pod `CreateContainerConfigError` | The chart runs read-only root + non-root uid 1000. Give the app an `extraVolumes` emptyDir (see `apps/podinfo/values.yaml`) |
| App missing from Argo CD | It only sees pushed commits. `kubectl -n argocd describe applicationset lab-apps` |
| Ingress rejected: "host and path is already defined" | Two Ingresses claim the same host+path. Usually an app rename — `helm uninstall <app> -n <ns>` first, then deploy |
| Logs stop arriving; `failed to create fsnotify watcher: too many open files` | Host inotify limits exhausted. `sudo sysctl -w fs.inotify.max_user_instances=512 fs.inotify.max_user_watches=524288` — `make preflight` warns about this |
| Grafana shows no app in the dropdown | The app has no pods yet, or kube-state-metrics has not scraped it — wait a scrape interval |
| Prometheus target missing after enabling metrics | `kubectl -n <ns> get servicemonitor` — then check `metrics.path` and that the port name resolves |
| HPA shows `<unknown>` targets | `kubectl -n kube-system get deploy metrics-server` |

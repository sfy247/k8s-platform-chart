# k8s-platform-chart

Reusable Kubernetes Helm chart — deploy **any containerised app** with a single chart.

## Quick start

```bash
# Minimum — just image + port
helm install my-app ./charts/generic-app \
  --set image.repository=ghcr.io/sfy247/my-app \
  --set image.tag=latest \
  --set service.targetPort=3000

# Full prod deploy
helm install sfy247-website ./charts/generic-app \
  -f examples/nodejs-app.yaml \
  -f environments/prod.yaml
```

## Chart features

| Feature | Toggle |
|---|---|
| Deployment + Service | always on |
| Ingress | `ingress.enabled: true` |
| Horizontal Pod Autoscaler | `autoscaling.enabled: true` |
| Pod Disruption Budget | `podDisruptionBudget.enabled: true` |
| Persistent Volume Claim | `persistence.enabled: true` |
| ConfigMap env injection | `configMap.enabled: true` |
| Network Policy | `networkPolicy.enabled: true` |
| Sidecar containers | `extraContainers: [...]` |
| Init containers | `extraInitContainers: [...]` |

## Minimum required values

```yaml
image:
  repository: ghcr.io/your-org/your-app
  tag: "1.0.0"

service:
  targetPort: 3000   # port your app listens on
```

Everything else has a safe default.

---

## How it works

### The big picture

When you run `helm install`, Helm reads your `values.yaml` plus any override files and renders all the templates into raw Kubernetes YAML, then sends them to the cluster. The cluster's control plane takes over from there.

```
Your App Image
     │
     ▼
values.yaml  ──►  Helm  ──►  Kubernetes API  ──►  Cluster
     +                          (rendered YAML)
 overrides
```

---

### `Chart.yaml` — Identity card

The chart's name, version, and description. Kubernetes never sees this — Helm uses it to identify and version the chart when you install or upgrade.

---

### `values.yaml` — The contract

This is the interface between the chart and the caller. Every configurable thing lives here with a safe default. When you deploy an app you only override what you need:

```
values.yaml (defaults)
     +
examples/nodejs-app.yaml  (app-specific: image, port, env vars)
     +
environments/prod.yaml    (env-specific: replicas, HPA, PDB)
     │
     ▼
Helm merges all three — last file wins on conflicts
```

---

### `_helpers.tpl` — Name factory

A library of reusable name functions. Every template calls these instead of hardcoding names:

```
generic-app.fullname      →  "sfy247-website-generic-app"
generic-app.labels        →  app.kubernetes.io/name, instance, version, managed-by
generic-app.selectorLabels  →  the subset used to match pods to Services and HPAs
```

The labels matter — Services and HPAs find pods purely by matching these labels.

---

### `deployment.yaml` — The core

This is the main resource. It tells Kubernetes *what* to run and *how*:

```
Deployment
 └── manages ReplicaSet
      └── manages N Pods
           └── Container(s)
                ├── your image
                ├── env vars (from values.env + ConfigMap)
                ├── resource limits (CPU/memory)
                ├── health probes
                ├── security context
                └── volume mounts
```

**Security context** — every pod runs with:

```yaml
runAsNonRoot: true               # cannot run as root
readOnlyRootFilesystem: true     # cannot write to the container filesystem
allowPrivilegeEscalation: false
capabilities:
  drop: [ALL]                    # no Linux capabilities at all
```

This is CIS Kubernetes Benchmark compliant out of the box.

**Checksum annotation** — if a ConfigMap changes, pods automatically restart:

```yaml
checksum/config: {{ include configmap.yaml | sha256sum }}
```

Without this, pods would keep running with stale config after a `helm upgrade`.

**Graceful shutdown** — `terminationGracePeriodSeconds` plus the `preStop` hook in `prod.yaml` give the app time to finish in-flight requests before Kubernetes kills the pod.

---

### `service.yaml` — Network entry point

Exposes the pod(s) on a stable internal DNS name and IP. Pods are ephemeral (they get new IPs on restart) but the Service IP never changes.

```
Client  →  Service (stable IP: 10.96.x.x)  →  Pod A
                                             →  Pod B   (load balanced)
                                             →  Pod C
```

The selector links the Service to the right pods via the same labels from `_helpers.tpl`.

---

### `ingress.yaml` — External traffic

Routes external HTTP/HTTPS traffic into the cluster. Only created when `ingress.enabled: true`.

```
Internet  →  Load Balancer  →  Ingress Controller (nginx)  →  Service  →  Pods
                                     │
                               reads Ingress rules:
                               host: myapp.example.com
                               path: /  →  service:80
```

TLS terminates at the Ingress — traffic inside the cluster travels unencrypted to the Service.

---

### `hpa.yaml` — Auto scaling

Watches CPU (and optionally memory) and adjusts replica count automatically. Only active when `autoscaling.enabled: true`.

```
HPA watches  →  Pod CPU usage
                    │
               > 70% sustained  →  scale up   (add pods)
               < 70% sustained  →  scale down  (remove pods)
                    │
               bounded by minReplicas / maxReplicas
```

When HPA is enabled, it owns the replica count — that's why the Deployment template skips the static `replicaCount` field:

```yaml
{{- if not .Values.autoscaling.enabled }}
replicas: {{ .Values.replicaCount }}
{{- end }}
```

---

### `pdb.yaml` — Availability during disruptions

Tells Kubernetes "never take down more than X pods at once" during voluntary disruptions (node drain, cluster upgrade, rolling update).

```
Without PDB:  node drain  →  all pods could go down simultaneously
With PDB:     minAvailable: 1  →  at least 1 pod always stays up
```

Only matters at 2+ replicas. That's why it's enabled in `prod.yaml` and disabled in `dev.yaml`.

---

### `serviceaccount.yaml` — Pod identity

Gives your app a named Kubernetes identity. The main use is attaching cloud IAM roles so the pod can talk to AWS S3, GCS, etc. without storing credentials in the image:

```yaml
serviceAccount:
  annotations:
    eks.amazonaws.com/role-arn: arn:aws:iam::123:role/my-app-role
```

`automountServiceAccountToken: false` is set by default — the pod cannot talk to the Kubernetes API unless you explicitly enable it.

---

### `configmap.yaml` — Non-secret config

Key/value pairs injected as environment variables. Separated from the Deployment so config changes don't require a new image build — just a `helm upgrade`.

```
ConfigMap  →  envFrom: configMapRef  →  env vars inside container
```

---

### `networkpolicy.yaml` — Traffic rules

By default pods can talk to anything in the cluster. When `networkPolicy.enabled: true`, you lock it down so only allowed pods can reach yours:

```
All pods     →  ✗  →  your pod   (blocked)
Allowed pods →  ✓  →  your pod
```

---

### `pvc.yaml` — Persistent storage

Kubernetes pods are stateless by default — data written to the filesystem disappears on restart. A PersistentVolumeClaim mounts durable storage that survives pod restarts:

```
Pod restarts  →  new pod mounts the same PVC  →  data still there
```

Not needed for stateless apps (APIs, frontends). Required for databases, file uploads, and anything that writes to disk.

---

## Environment layering

```bash
helm install sfy247-website ./charts/generic-app \
  -f examples/nodejs-app.yaml \    # layer 1: app config
  -f environments/prod.yaml         # layer 2: env config (wins on conflict)
```

Helm deep-merges left to right. `prod.yaml` can raise `replicaCount` without touching the app file.

| File | Purpose |
|---|---|
| `examples/*.yaml` | App-specific config (image, port, env vars) |
| `environments/dev.yaml` | Relaxed limits, single replica, no PDB |
| `environments/staging.yaml` | Mirrors prod config at lower scale |
| `environments/prod.yaml` | HA, PDB enforced, HPA enabled, graceful shutdown |

This means:
- **App team** owns `examples/*.yaml` — image, port, env vars, health paths
- **Platform team** owns `environments/*.yaml` — resource limits, HA settings, security

---

## Request flow end to end

```
1. Browser hits myapp.example.com
2. DNS resolves to cloud Load Balancer IP
3. Load Balancer forwards to Ingress Controller pod
4. Ingress Controller reads Ingress rules → routes to Service
5. Service load-balances across healthy Pods
   (only pods passing the readiness probe receive traffic)
6. Pod handles the request
7. Response travels back the same path
```

If a pod fails its **liveness probe**, Kubernetes restarts it.
If a pod fails its **readiness probe**, it is removed from the Service rotation until it recovers — no traffic hits a broken pod.

---

## Security defaults

All pods run with:
- `runAsNonRoot: true`
- `readOnlyRootFilesystem: true`
- `allowPrivilegeEscalation: false`
- `capabilities: drop: [ALL]`
- `automountServiceAccountToken: false`

---

## Adding to Backstage catalog

```yaml
catalog:
  locations:
    - type: url
      target: https://github.com/sfy247/k8s-platform-chart/blob/main/catalog-info.yaml
```

---

## Repo structure

```
k8s-platform-chart/
├── charts/generic-app/     # the Helm chart
│   ├── Chart.yaml
│   ├── values.yaml
│   └── templates/
│       ├── _helpers.tpl
│       ├── deployment.yaml
│       ├── service.yaml
│       ├── ingress.yaml
│       ├── hpa.yaml
│       ├── pdb.yaml
│       ├── serviceaccount.yaml
│       ├── configmap.yaml
│       ├── networkpolicy.yaml
│       └── pvc.yaml
├── examples/               # per-app values files
├── environments/           # dev / staging / prod overrides
└── .github/workflows/      # CI: helm lint + template on every PR
```

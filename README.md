# k8s-platform-chart

Reusable Kubernetes Helm chart — deploy **any containerised app** with a single chart.

## Quick start

```bash
# Deploy a Node.js app to dev
helm install my-app ./charts/generic-app \
  --set image.repository=ghcr.io/sfy247/my-app \
  --set image.tag=latest \
  --set service.targetPort=3000 \
  -f environments/dev.yaml

# Deploy with a pre-built example values file
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

## Environment layering

Layer values files to separate app config from environment config:

```bash
helm install my-app ./charts/generic-app \
  -f examples/nodejs-app.yaml \   # app-specific values
  -f environments/prod.yaml        # environment overrides (applied last)
```

| File | Purpose |
|---|---|
| `examples/*.yaml` | App-specific config (image, port, env vars) |
| `environments/dev.yaml` | Relaxed limits, single replica, no PDB |
| `environments/staging.yaml` | Mirrors prod config at lower scale |
| `environments/prod.yaml` | HA, PDB enforced, HPA enabled, graceful shutdown |

## Security defaults

All pods run with:
- `runAsNonRoot: true`
- `readOnlyRootFilesystem: true`
- `allowPrivilegeEscalation: false`
- `capabilities: drop: [ALL]`
- `automountServiceAccountToken: false`

## Adding to Backstage catalog

Register this chart in your Backstage instance by adding a URL location in `app-config.yaml`:

```yaml
catalog:
  locations:
    - type: url
      target: https://github.com/sfy247/k8s-platform-chart/blob/main/catalog-info.yaml
```

## Repo structure

```
k8s-platform-chart/
├── charts/generic-app/     # the Helm chart
├── examples/               # per-app values files
├── environments/           # per-environment overrides
└── .github/workflows/      # CI: helm lint + template on every PR
```

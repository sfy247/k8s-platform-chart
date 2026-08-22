# apps/

One directory per deployed application. Everything in here is reconciled into
the lab cluster by the `lab-apps` ApplicationSet
(`lab/platform/bootstrap/applicationset.yaml`).

```
apps/<name>/
├── app.yaml      # registration: name, namespace, environment
└── values.yaml   # Helm values for charts/generic-app
```

## Adding an app

```bash
make new-app NAME=myapp PORT=3000 IMAGE=ghcr.io/sfy247/myapp
$EDITOR apps/myapp/values.yaml        # image, port, probe paths
make deploy APP=myapp                 # try it from the working tree
git add apps/myapp && git commit -m "feat: add myapp" && git push
```

After the push, Argo CD creates the Application and syncs it (~60s, or force
with `make sync APP=myapp`). The app is then at
`http://myapp.localtest.me:8090`.

## How the values layer

The ApplicationSet renders `charts/generic-app` with two values files, later
wins:

```
charts/generic-app/values.yaml      defaults for everything
        ↓
environments/<environment>.yaml     per-environment policy (local/dev/staging/prod)
        ↓
apps/<name>/values.yaml             this app's settings
```

So `app.yaml`'s `environment:` picks the policy layer, and `values.yaml` only
has to carry what is genuinely app-specific.

## Removing an app

Delete the directory and push. `prune: true` means Argo CD removes the
workload from the cluster — it does **not** delete the namespace or any
PersistentVolumeClaims.

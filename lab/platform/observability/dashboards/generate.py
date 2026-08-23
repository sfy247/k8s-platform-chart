#!/usr/bin/env python3
"""Generate the lab's Grafana dashboards.

The JSON files next to this script are what Argo CD delivers — they are the
source of truth for the cluster. This script exists so the JSON stays
readable and consistent instead of being hand-edited.

    python3 lab/platform/observability/dashboards/generate.py

Every query here is validated against a live Prometheus before commit; see
the README section in lab/README.md.
"""

from __future__ import annotations

import json
import pathlib

PROM = "${ds_prom}"
HERE = pathlib.Path(__file__).parent

# Namespaces that hold platform components rather than user applications.
PLATFORM_NS = "kube-system|observability|ingress-nginx|argocd|kube-public|kube-node-lease"


# ── panel builders ────────────────────────────────────────────────────────
def tgt(expr: str, legend: str = "", ref: str = "A", instant: bool = False) -> dict:
    return {
        "datasource": {"type": "prometheus", "uid": PROM},
        "editorMode": "code",
        "expr": expr,
        "legendFormat": legend,
        "range": not instant,
        "instant": instant,
        "refId": ref,
    }


def steps(*pairs) -> dict:
    return {"mode": "absolute", "steps": [{"color": c, "value": v} for c, v in pairs]}


def stat(title, expr, unit, x, y, w=4, h=4, decimals=None, thresholds=None, desc=""):
    return {
        "type": "stat", "title": title, "description": desc,
        "gridPos": {"h": h, "w": w, "x": x, "y": y},
        "datasource": {"type": "prometheus", "uid": PROM},
        "targets": [tgt(expr, "", instant=True)],
        "options": {
            "reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": False},
            "colorMode": "value", "graphMode": "none", "textMode": "auto",
        },
        "fieldConfig": {
            "defaults": {
                "unit": unit, "decimals": decimals,
                "thresholds": thresholds or steps(("text", None)),
            },
            "overrides": [],
        },
    }


def ts(title, targets, unit, x, y, w=12, h=8, desc="", stack=False, maxv=None, minv=0):
    return {
        "type": "timeseries", "title": title, "description": desc,
        "gridPos": {"h": h, "w": w, "x": x, "y": y},
        "datasource": {"type": "prometheus", "uid": PROM},
        "targets": targets,
        "fieldConfig": {
            "defaults": {
                "unit": unit, "min": minv, "max": maxv,
                "custom": {
                    "drawStyle": "line", "lineWidth": 2,
                    "fillOpacity": 25 if stack else 8,
                    "showPoints": "never", "spanNulls": True,
                    "stacking": {"mode": "normal" if stack else "none"},
                },
                "thresholds": steps(("green", None)),
            },
            "overrides": [],
        },
        "options": {
            "legend": {"displayMode": "list", "placement": "bottom", "showLegend": True},
            "tooltip": {"mode": "multi", "sort": "desc"},
        },
    }


def gauge(title, expr, x, y, w=4, h=5, thresholds=None, desc=""):
    return {
        "type": "gauge", "title": title, "description": desc,
        "gridPos": {"h": h, "w": w, "x": x, "y": y},
        "datasource": {"type": "prometheus", "uid": PROM},
        "targets": [tgt(expr, "", instant=True)],
        "options": {"reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": False},
                    "showThresholdLabels": False, "showThresholdMarkers": True},
        "fieldConfig": {"defaults": {"unit": "percentunit", "min": 0, "max": 1,
                        "thresholds": thresholds or steps(("green", None), ("orange", 0.75), ("red", 0.9))},
                        "overrides": []},
    }


def table(title, targets, x, y, w=24, h=8, desc="", overrides=None, transforms=None):
    return {
        "type": "table", "title": title, "description": desc,
        "gridPos": {"h": h, "w": w, "x": x, "y": y},
        "datasource": {"type": "prometheus", "uid": PROM},
        "targets": targets,
        "transformations": transforms or [{"id": "merge", "options": {}}],
        "fieldConfig": {"defaults": {"custom": {"align": "auto", "filterable": True}},
                        "overrides": overrides or []},
        "options": {"showHeader": True, "footer": {"show": False}},
    }


def row(title, y):
    return {"type": "row", "title": title, "gridPos": {"h": 1, "w": 24, "x": 0, "y": y},
            "collapsed": False, "panels": []}


def dashboard(uid, title, description, tags, panels, templating=None, refresh="30s"):
    return {
        "uid": uid, "title": title, "description": description, "tags": tags,
        "timezone": "browser", "schemaVersion": 39, "version": 1, "editable": True,
        "refresh": refresh, "time": {"from": "now-3h", "to": "now"},
        "templating": {"list": templating or [
            {"name": "ds_prom", "label": "Metrics source", "type": "datasource",
             "query": "prometheus", "current": {}, "hide": 0, "refresh": 1},
        ]},
        "panels": panels,
    }


def write(d: dict, filename: str) -> None:
    path = HERE / filename
    path.write_text(json.dumps(d, indent=2) + "\n")
    print(f"  {filename}: {len([p for p in d['panels'] if p['type'] != 'row'])} panels")


# ══════════════════════════════════════════════════════════════════════════
# 1. Cluster Overview — is the cluster healthy, and can it take more work?
# ══════════════════════════════════════════════════════════════════════════
def cluster_overview() -> dict:
    p = []
    p.append(row("Health at a glance", 0))
    p.append(stat("Nodes Ready", 'sum(kube_node_status_condition{condition="Ready",status="true"})',
                  "short", 0, 1, thresholds=steps(("red", None), ("green", 1)),
                  desc="Nodes reporting Ready. Anything below your node count means capacity is gone."))
    p.append(stat("Pods Running", 'sum(kube_pod_status_phase{phase="Running"})', "short", 4, 1,
                  thresholds=steps(("green", None))))
    p.append(stat("Pods Pending", 'sum(kube_pod_status_phase{phase="Pending"})', "short", 8, 1,
                  thresholds=steps(("green", None), ("orange", 1)),
                  desc="Pending means the scheduler cannot place them — usually no node has enough CPU/memory left."))
    p.append(stat("Pods Failed", 'sum(kube_pod_status_phase{phase="Failed"})', "short", 12, 1,
                  thresholds=steps(("green", None), ("red", 1))))
    p.append(stat("Restarts (1h)", 'sum(increase(kube_pod_container_status_restarts_total[1h]))', "short", 16, 1,
                  decimals=0, thresholds=steps(("green", None), ("orange", 5), ("red", 20)),
                  desc="Container restarts cluster-wide. A steady climb is a crash loop somewhere."))
    p.append(stat("Firing alerts", 'count(ALERTS{alertstate="firing",alertname!="Watchdog"}) or vector(0)',
                  "short", 20, 1, thresholds=steps(("green", None), ("orange", 1)),
                  desc="Watchdog is excluded — it fires permanently on purpose."))

    p.append(row("Capacity — can the cluster take more work?", 5))
    p.append(gauge("CPU committed",
                   'sum(kube_pod_container_resource_requests{resource="cpu"}) / sum(kube_node_status_allocatable{resource="cpu"})',
                   0, 6, desc="Requests vs allocatable. This is what the SCHEDULER cares about — at 100% nothing new can be placed, however idle the CPUs look."))
    p.append(gauge("Memory committed",
                   'sum(kube_pod_container_resource_requests{resource="memory"}) / sum(kube_node_status_allocatable{resource="memory"})',
                   4, 6))
    p.append(ts("CPU: used vs requested vs allocatable", [
        tgt('sum(rate(container_cpu_usage_seconds_total{container!=""}[5m]))', "actually used"),
        tgt('sum(kube_pod_container_resource_requests{resource="cpu"})', "requested (reserved)", "B"),
        tgt('sum(kube_node_status_allocatable{resource="cpu"})', "allocatable", "C"),
    ], "short", 8, 6, w=16, h=5,
        desc="The gap between 'used' and 'requested' is waste: reserved capacity nobody is using."))

    p.append(ts("Memory: used vs requested vs allocatable", [
        tgt('sum(container_memory_working_set_bytes{container!=""})', "actually used"),
        tgt('sum(kube_pod_container_resource_requests{resource="memory"})', "requested (reserved)", "B"),
        tgt('sum(kube_node_status_allocatable{resource="memory"})', "allocatable", "C"),
    ], "bytes", 0, 11, w=12))
    p.append(ts("Pods per node", [
        tgt('count by (node) (kube_pod_info{node!=""})', "{{node}}"),
    ], "short", 12, 11, w=12, desc="Nodes have a pod cap (110 by default) independent of CPU and memory."))

    p.append(row("Nodes", 19))
    p.append(ts("CPU usage by node", [
        tgt('sum by (node) (rate(container_cpu_usage_seconds_total{container!=""}[5m]))', "{{node}}"),
    ], "short", 0, 20, w=8))
    p.append(ts("Memory usage by node", [
        tgt('sum by (node) (container_memory_working_set_bytes{container!=""})', "{{node}}"),
    ], "bytes", 8, 20, w=8))
    p.append(ts("Node disk used (kubelet volume)", [
        tgt('1 - (node_filesystem_avail_bytes{mountpoint="/var/lib/kubelet"} '
            '/ node_filesystem_size_bytes{mountpoint="/var/lib/kubelet"})', "{{instance}}"),
    ], "percentunit", 16, 20, w=8, maxv=1,
        desc="k3d nodes are containers, so there is no / mountpoint to graph — node-exporter "
             "reports the bind-mounted paths instead. /var/lib/kubelet is the one that matters: "
             "it holds pod storage and is what the kubelet's eviction manager watches. On k3d "
             "every node shares the Docker host's disk, so all three lines move together."))

    p.append(row("Problems", 28))
    p.append(table("Pods not Running", [
        tgt('sum by (namespace, pod, phase) (kube_pod_status_phase{phase!="Running",phase!="Succeeded"}) > 0', "", instant=True),
    ], 0, 29, w=12, desc="Empty is good. Anything here is stuck Pending, Failed or Unknown."))
    p.append(ts("Containers restarting (top 10)", [
        tgt('topk(10, sum by (namespace, pod) (increase(kube_pod_container_status_restarts_total[15m]))) > 0', "{{namespace}}/{{pod}}"),
    ], "short", 12, 29, w=12))
    p.append(ts("CPU throttling by workload (top 10)", [
        tgt('topk(10, sum by (namespace, pod) (rate(container_cpu_cfs_throttled_periods_total[5m])) '
            '/ clamp_min(sum by (namespace, pod) (rate(container_cpu_cfs_periods_total[5m])), 0.001)) > 0',
            "{{namespace}}/{{pod}}"),
    ], "percentunit", 0, 37, w=12, maxv=1,
        desc="The share of CPU periods where the container hit its limit and was paused. High throttling means a CPU limit is too low — it shows up as latency, not as high CPU usage."))
    p.append(ts("Persistent volume usage", [
        tgt('kubelet_volume_stats_used_bytes / kubelet_volume_stats_capacity_bytes', "{{namespace}}/{{persistentvolumeclaim}}"),
    ], "percentunit", 12, 37, w=12, maxv=1,
        desc="A full PVC on Prometheus or Loki means telemetry stops being recorded."))
    return dashboard("lab-cluster-overview", "Cluster Overview",
                     "Is the cluster healthy, and can it take more work?",
                     ["lab", "cluster"], p)


# ══════════════════════════════════════════════════════════════════════════
# 2. Fleet — every application on one page, no dropdowns
# ══════════════════════════════════════════════════════════════════════════
# Joins container metrics to the app name through kube_pod_labels, which is
# why kube-state-metrics allow-lists app.kubernetes.io/instance.
BY_APP = ('* on (namespace, pod) group_left(label_app_kubernetes_io_instance) '
          'kube_pod_labels{label_app_kubernetes_io_instance!=""}')
APP_LEGEND = "{{label_app_kubernetes_io_instance}}"


def fleet() -> dict:
    p = []
    p.append(row("Every app at a glance", 0))
    p.append(stat("Applications", 'count(count by (label_app_kubernetes_io_instance) '
                  f'(kube_pod_labels{{namespace!~"{PLATFORM_NS}",label_app_kubernetes_io_instance!=""}}))',
                  "short", 0, 1, thresholds=steps(("text", None))))
    p.append(stat("Total request rate", 'sum(rate(nginx_ingress_controller_requests[5m]))', "reqps", 4, 1, decimals=2))
    p.append(stat("5xx rate", 'sum(rate(nginx_ingress_controller_requests{status=~"5.."}[5m]))', "reqps", 8, 1,
                  decimals=3, thresholds=steps(("green", None), ("orange", 0.01), ("red", 0.1))))
    p.append(stat("Deployments not fully ready",
                  f'count(kube_deployment_status_replicas_ready{{namespace!~"{PLATFORM_NS}"}} '
                  f'< kube_deployment_spec_replicas{{namespace!~"{PLATFORM_NS}"}}) or vector(0)',
                  "short", 12, 1, thresholds=steps(("green", None), ("red", 1))))
    p.append(stat("Restarts (1h)",
                  f'sum(increase(kube_pod_container_status_restarts_total{{namespace!~"{PLATFORM_NS}"}}[1h])) or vector(0)',
                  "short", 16, 1, decimals=0, thresholds=steps(("green", None), ("orange", 3))))
    p.append(stat("OOMKilled (24h)",
                  'sum(kube_pod_container_status_last_terminated_reason{reason="OOMKilled"}) or vector(0)',
                  "short", 20, 1, thresholds=steps(("green", None), ("red", 1))))

    p.append(row("Traffic — per app, from the ingress", 5))
    p.append(ts("Request rate", [
        tgt('sum by (ingress) (rate(nginx_ingress_controller_requests[5m]))', "{{ingress}}"),
    ], "reqps", 0, 6, w=8))
    p.append(ts("Error rate (5xx share)", [
        tgt('sum by (ingress) (rate(nginx_ingress_controller_requests{status=~"5.."}[5m])) '
            '/ clamp_min(sum by (ingress) (rate(nginx_ingress_controller_requests[5m])), 0.001)',
            "{{ingress}}"),
    ], "percentunit", 8, 6, w=8, maxv=1))
    p.append(ts("p95 latency", [
        tgt('histogram_quantile(0.95, sum by (ingress, le) '
            '(rate(nginx_ingress_controller_request_duration_seconds_bucket[5m])))', "{{ingress}}"),
    ], "s", 16, 6, w=8))

    p.append(row("Resources — per app", 14))
    p.append(ts("CPU usage", [
        tgt(f'sum by (label_app_kubernetes_io_instance) (rate(container_cpu_usage_seconds_total{{container!=""}}[5m]) {BY_APP})',
            APP_LEGEND),
    ], "short", 0, 15, w=8))
    p.append(ts("Memory working set", [
        tgt(f'sum by (label_app_kubernetes_io_instance) (container_memory_working_set_bytes{{container!=""}} {BY_APP})',
            APP_LEGEND),
    ], "bytes", 8, 15, w=8))
    p.append(ts("Memory vs limit", [
        tgt(f'sum by (label_app_kubernetes_io_instance) (container_memory_working_set_bytes{{container!=""}} {BY_APP}) '
            f'/ clamp_min(sum by (label_app_kubernetes_io_instance) '
            f'(kube_pod_container_resource_limits{{resource="memory"}} {BY_APP}), 1)', APP_LEGEND),
    ], "percentunit", 16, 15, w=8, maxv=1,
        desc="Approaching 1 means an OOMKill is coming. This is the graph to check before raising a limit."))

    p.append(row("Workload state", 23))
    p.append(table("Deployments — ready vs desired", [
        tgt(f'sum by (namespace, deployment) (kube_deployment_status_replicas_ready{{namespace!~"{PLATFORM_NS}"}})',
            "ready", "A", instant=True),
        tgt(f'sum by (namespace, deployment) (kube_deployment_spec_replicas{{namespace!~"{PLATFORM_NS}"}})',
            "desired", "B", instant=True),
    ], 0, 24, w=12))
    p.append(ts("Restarts by pod (15m)", [
        tgt(f'sum by (namespace, pod) (increase(kube_pod_container_status_restarts_total{{namespace!~"{PLATFORM_NS}"}}[15m])) > 0',
            "{{namespace}}/{{pod}}"),
    ], "short", 12, 24, w=12))
    return dashboard("lab-fleet", "Fleet — All Applications",
                     "Every application on one page. No dropdown — this is the 'is anything wrong' view.",
                     ["lab", "application"], p)


# ══════════════════════════════════════════════════════════════════════════
# 3. Platform Health — the machinery everything else depends on
# ══════════════════════════════════════════════════════════════════════════
def platform_health() -> dict:
    p = []
    p.append(row("Argo CD — is desired state actually applied?", 0))
    p.append(stat("Applications", 'count(argocd_app_info)', "short", 0, 1, thresholds=steps(("text", None))))
    p.append(stat("Synced", 'count(argocd_app_info{sync_status="Synced"}) or vector(0)', "short", 4, 1,
                  thresholds=steps(("green", None))))
    p.append(stat("OutOfSync", 'count(argocd_app_info{sync_status="OutOfSync"}) or vector(0)', "short", 8, 1,
                  thresholds=steps(("green", None), ("orange", 1)),
                  desc="Git and the cluster disagree. Either a sync is in flight or something is blocking it."))
    p.append(stat("Healthy", 'count(argocd_app_info{health_status="Healthy"}) or vector(0)', "short", 12, 1,
                  thresholds=steps(("green", None))))
    p.append(stat("Degraded", 'count(argocd_app_info{health_status="Degraded"}) or vector(0)', "short", 16, 1,
                  thresholds=steps(("green", None), ("red", 1))))
    p.append(stat("Sync failures (1h)",
                  'sum(increase(argocd_app_sync_total{phase!="Succeeded"}[1h])) or vector(0)', "short", 20, 1,
                  decimals=0, thresholds=steps(("green", None), ("orange", 1))))
    p.append(table("Applications", [
        tgt('argocd_app_info', "", instant=True),
    ], 0, 5, w=24, h=7,
        desc="Every Application Argo CD manages, with its sync and health status.",
        transforms=[{"id": "organize", "options": {"excludeByName": {
            "Time": True, "__name__": True, "container": True, "endpoint": True,
            "instance": True, "job": True, "pod": True, "service": True, "Value": True,
            "autosync_enabled": True, "namespace": True}}}]))

    p.append(row("Ingress — the front door", 12))
    p.append(ts("Requests by status class", [
        tgt('sum by (status) (rate(nginx_ingress_controller_requests[5m]))', "{{status}}"),
    ], "reqps", 0, 13, w=8, stack=True))
    p.append(ts("Latency percentiles (all hosts)", [
        tgt('histogram_quantile(0.50, sum by (le) (rate(nginx_ingress_controller_request_duration_seconds_bucket[5m])))', "p50"),
        tgt('histogram_quantile(0.95, sum by (le) (rate(nginx_ingress_controller_request_duration_seconds_bucket[5m])))', "p95", "B"),
        tgt('histogram_quantile(0.99, sum by (le) (rate(nginx_ingress_controller_request_duration_seconds_bucket[5m])))', "p99", "C"),
    ], "s", 8, 13, w=8))
    p.append(stat("Last config reload OK",
                  'min(nginx_ingress_controller_config_last_reload_successful)', "short", 16, 13, w=4, h=4,
                  thresholds=steps(("red", None), ("green", 1)),
                  desc="0 means nginx rejected its own generated config — routing is frozen at the last good version."))
    p.append(stat("Ingress hosts served",
                  'count(count by (ingress) (nginx_ingress_controller_requests))', "short", 20, 13, w=4, h=4))

    p.append(row("Telemetry pipeline — can you still see anything?", 21))
    p.append(stat("Scrape targets up", 'sum(up)', "short", 0, 22, thresholds=steps(("text", None))))
    p.append(stat("Targets DOWN", 'count(up == 0) or vector(0)', "short", 4, 22,
                  thresholds=steps(("green", None), ("red", 1)),
                  desc="A down target is a blind spot — metrics for that component simply stop."))
    p.append(stat("Series in memory", 'prometheus_tsdb_head_series', "short", 8, 22,
                  desc="Cardinality. Sudden growth usually means someone added a high-cardinality label."))
    p.append(stat("Samples ingested/s", 'sum(rate(prometheus_tsdb_head_samples_appended_total[5m]))', "short", 12, 22, decimals=0))
    p.append(stat("Loki ingest rate",
                  'sum(rate(loki_distributor_lines_received_total[5m])) or sum(rate(loki_ingester_streams_created_total[5m])) or vector(0)',
                  "short", 16, 22, decimals=2, desc="Log lines per second reaching Loki. Zero while apps are running means collection has stopped."))
    p.append(stat("Alloy collectors", 'count(up{job=~".*alloy.*"}) or vector(0)', "short", 20, 22,
                  desc="Should equal the node count — Alloy is a DaemonSet."))
    p.append(ts("Scrape duration by job", [
        tgt('topk(10, max by (job) (scrape_duration_seconds))', "{{job}}"),
    ], "s", 0, 26, w=12, desc="A job creeping toward its timeout will start failing intermittently."))
    p.append(ts("Firing alerts", [
        tgt('count by (alertname) (ALERTS{alertstate="firing", alertname!="Watchdog"})', "{{alertname}}"),
    ], "short", 12, 26, w=12))
    return dashboard("lab-platform-health", "Platform Health",
                     "Argo CD, ingress and the telemetry pipeline — the machinery everything else depends on.",
                     ["lab", "platform"], p)


if __name__ == "__main__":
    print("generating dashboards:")
    write(cluster_overview(), "cluster-overview.json")
    write(fleet(), "fleet.json")
    write(platform_health(), "platform-health.json")

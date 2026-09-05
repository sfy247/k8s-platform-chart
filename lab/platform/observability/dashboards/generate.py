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


def stat(title, expr, unit, x, y, w=4, h=4, decimals=None, thresholds=None, desc="", no_value=None):
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
                **({"noValue": no_value} if no_value else {}),
            },
            "overrides": [],
        },
    }


def ts(title, targets, unit, x, y, w=12, h=8, desc="", stack=False, maxv=None, minv=0, no_value=None):
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




# ══════════════════════════════════════════════════════════════════════════
# 4. PostgreSQL — the shared database, metrics and logs on one page
# ══════════════════════════════════════════════════════════════════════════
LOKI = "${ds_loki}"
PG = 'job="data/lab-pg"'


def loki_tgt(expr: str, legend: str = "", ref: str = "A") -> dict:
    return {
        "datasource": {"type": "loki", "uid": LOKI},
        "editorMode": "code", "expr": expr, "legendFormat": legend,
        "queryType": "range", "refId": ref,
    }


def logs_panel(title, expr, x, y, w=24, h=12, desc=""):
    return {
        "type": "logs", "title": title, "description": desc,
        "gridPos": {"h": h, "w": w, "x": x, "y": y},
        "datasource": {"type": "loki", "uid": LOKI},
        "targets": [loki_tgt(expr)],
        "options": {
            "showTime": True, "sortOrder": "Descending", "wrapLogMessage": True,
            "enableLogDetails": True, "dedupStrategy": "none", "prettifyLogMessage": True,
        },
    }


def loki_ts(title, targets, x, y, w=12, h=7, desc="", stack=True):
    return {
        "type": "timeseries", "title": title, "description": desc,
        "gridPos": {"h": h, "w": w, "x": x, "y": y},
        "datasource": {"type": "loki", "uid": LOKI},
        "targets": targets,
        "fieldConfig": {"defaults": {"custom": {
            "drawStyle": "bars", "fillOpacity": 70, "lineWidth": 0,
            "stacking": {"mode": "normal" if stack else "none"}}}, "overrides": []},
        "options": {"legend": {"displayMode": "list", "placement": "bottom", "showLegend": True},
                    "tooltip": {"mode": "multi"}},
    }


def postgres() -> dict:
    p = []

    # ── Availability ──────────────────────────────────────────────────────
    p.append(row("Availability", 0))
    p.append(stat("Cluster up", f'max(cnpg_collector_up{{{PG}}})', "short", 0, 1,
                  thresholds=steps(("red", None), ("green", 1)),
                  desc="1 when the metrics collector can reach PostgreSQL. 0 means the database is not answering, whatever the pod says."))
    p.append(stat("Instances ready", f'count(cnpg_collector_up{{{PG}}} == 1)', "short", 4, 1,
                  thresholds=steps(("red", None), ("green", 1))))
    p.append(stat("PostgreSQL version", f'max(cnpg_collector_postgres_version{{{PG}}})', "short", 8, 1,
                  decimals=2, thresholds=steps(("text", None))))
    p.append(stat("Uptime", f'time() - max(cnpg_pg_postmaster_start_time{{{PG}}})', "s", 12, 1,
                  desc="Time since the postmaster started. A sudden reset means a restart — check the logs below."))
    p.append(stat("Sync replicas", f'max(cnpg_collector_sync_replicas{{{PG}}}) or vector(0)', "short", 16, 1,
                  desc="0 is expected on a single-instance lab cluster; it means there is no replica to fail over to."))
    p.append(stat("Switchover required", f'max(cnpg_collector_manual_switchover_required{{{PG}}}) or vector(0)',
                  "short", 20, 1, thresholds=steps(("green", None), ("red", 1)),
                  desc="1 means the operator needs a human to promote an instance."))

    # ── Connections ───────────────────────────────────────────────────────
    p.append(row("Connections — the first thing to exhaust", 5))
    p.append(gauge("Connections used",
                   f'sum(cnpg_backends_total{{{PG}}}) / max(cnpg_pg_settings_setting{{{PG}, name="max_connections"}})',
                   0, 6, w=6, h=6,
                   desc="Against max_connections. Applications with oversized pools hit this long before the database is actually busy, and the failure looks like the app being down."))
    p.append(ts("Backends by database", [
        tgt(f'sum by (datname) (cnpg_backends_total{{{PG}}})', "{{datname}}"),
        tgt(f'max(cnpg_pg_settings_setting{{{PG}, name="max_connections"}})', "max_connections", "B"),
    ], "short", 6, 6, w=10, h=6))
    p.append(ts("Waiting backends", [
        tgt(f'sum by (datname) (cnpg_backends_waiting_total{{{PG}}})', "{{datname}}"),
    ], "short", 16, 6, w=8, h=6,
        desc="Backends blocked on a lock. A sustained non-zero value is contention, not load."))

    # ── Throughput ────────────────────────────────────────────────────────
    p.append(row("Throughput", 12))
    p.append(ts("Transactions per second", [
        tgt(f'sum by (datname) (rate(cnpg_pg_stat_database_xact_commit{{{PG}}}[5m]))', "{{datname}} commit"),
        tgt(f'sum by (datname) (rate(cnpg_pg_stat_database_xact_rollback{{{PG}}}[5m]))', "{{datname}} rollback", "B"),
    ], "short", 0, 13, w=8,
        desc="A rollback rate that tracks the commit rate usually means the application is failing, not the database."))
    p.append(ts("Rows read and written", [
        tgt(f'sum(rate(cnpg_pg_stat_database_tup_fetched{{{PG}}}[5m]))', "fetched"),
        tgt(f'sum(rate(cnpg_pg_stat_database_tup_returned{{{PG}}}[5m]))', "returned", "B"),
        tgt(f'sum(rate(cnpg_pg_stat_database_tup_inserted{{{PG}}}[5m]))', "inserted", "C"),
        tgt(f'sum(rate(cnpg_pg_stat_database_tup_updated{{{PG}}}[5m]))', "updated", "D"),
        tgt(f'sum(rate(cnpg_pg_stat_database_tup_deleted{{{PG}}}[5m]))', "deleted", "E"),
    ], "short", 8, 13, w=8,
        desc="'returned' far exceeding 'fetched' is the signature of sequential scans — rows examined but discarded."))
    p.append(ts("Deadlocks and conflicts", [
        tgt(f'sum by (datname) (increase(cnpg_pg_stat_database_deadlocks{{{PG}}}[15m]))', "{{datname}} deadlocks"),
        tgt(f'sum by (datname) (increase(cnpg_pg_stat_database_conflicts{{{PG}}}[15m]))', "{{datname}} conflicts", "B"),
    ], "short", 16, 13, w=8,
        desc="Any deadlock is a bug in lock ordering, not a capacity problem. Raising resources will not help."))

    # ── Cache and I/O ─────────────────────────────────────────────────────
    p.append(row("Cache and I/O", 21))
    p.append(gauge("Cache hit ratio",
                   f'sum(rate(cnpg_pg_stat_database_blks_hit{{{PG}}}[5m])) / '
                   f'clamp_min(sum(rate(cnpg_pg_stat_database_blks_hit{{{PG}}}[5m])) + '
                   f'sum(rate(cnpg_pg_stat_database_blks_read{{{PG}}}[5m])), 0.001)',
                   0, 22, w=6, h=6,
                   thresholds=steps(("red", None), ("orange", 0.90), ("green", 0.99)),
                   desc="Share of block reads served from shared_buffers. Below ~0.99 on a steady workload means the working set no longer fits in memory."))
    p.append(ts("Block reads: cache vs disk", [
        tgt(f'sum(rate(cnpg_pg_stat_database_blks_hit{{{PG}}}[5m]))', "from cache"),
        tgt(f'sum(rate(cnpg_pg_stat_database_blks_read{{{PG}}}[5m]))', "from disk", "B"),
    ], "short", 6, 22, w=10, h=6))
    p.append(ts("Temporary files", [
        tgt(f'sum(rate(cnpg_pg_stat_database_temp_bytes{{{PG}}}[5m]))', "bytes/s"),
    ], "Bps", 16, 22, w=8, h=6,
        desc="Queries spilling to disk because work_mem was too small for the sort or hash."))

    # ── Storage ───────────────────────────────────────────────────────────
    p.append(row("Storage and WAL", 28))
    p.append(ts("Database size", [
        tgt(f'cnpg_pg_database_size_bytes{{{PG}}}', "{{datname}}"),
    ], "bytes", 0, 29, w=8))
    p.append(ts("Volume usage", [
        tgt('kubelet_volume_stats_used_bytes{namespace="data"} / kubelet_volume_stats_capacity_bytes{namespace="data"}',
            "{{persistentvolumeclaim}}"),
    ], "percentunit", 8, 29, w=8, maxv=1,
        desc="A full volume stops writes. There are no backups configured, so recovering from that means losing data."))
    p.append(ts("WAL generated", [
        tgt(f'sum(rate(cnpg_collector_wal_bytes{{{PG}}}[5m]))', "bytes/s"),
    ], "Bps", 16, 29, w=8))
    p.append(ts("Checkpoints", [
        tgt(f'sum(increase(cnpg_pg_stat_checkpointer_checkpoints_timed{{{PG}}}[15m]))', "scheduled"),
        tgt(f'sum(increase(cnpg_pg_stat_checkpointer_checkpoints_req{{{PG}}}[15m]))', "requested", "B"),
    ], "short", 0, 37, w=12,
        desc="Requested checkpoints outnumbering scheduled ones means max_wal_size is too small for the write rate."))
    p.append(ts("Transaction ID age", [
        tgt(f'max by (datname) (cnpg_pg_database_xid_age{{{PG}}})', "{{datname}}"),
    ], "short", 12, 37, w=12,
        desc="Distance to transaction ID wraparound. Autovacuum keeps this bounded; sustained growth toward 2 billion is an emergency, not a warning."))

    # ── Logs ──────────────────────────────────────────────────────────────
    # CNPG wraps each PostgreSQL log line in JSON with the database's own
    # fields nested under `record`. Loki's json parser flattens that with
    # underscores, so the fields are record_error_severity, record_message
    # and so on — not record.error_severity.
    p.append(row("Logs — from the database itself", 45))
    p.append(loki_ts("Log volume by severity", [
        loki_tgt('sum by (record_error_severity) (count_over_time('
                 '{app="lab-pg", container="postgres"} | json | logger="postgres" [$__auto]))',
                 "{{record_error_severity}}"),
    ], 0, 46, w=12,
        desc="PostgreSQL's own severities: LOG, WARNING, ERROR, FATAL, PANIC."))
    p.append(loki_ts("Errors by SQLSTATE", [
        loki_tgt('sum by (record_sql_state_code) (count_over_time('
                 '{app="lab-pg", container="postgres"} | json | logger="postgres" '
                 '| record_error_severity =~ "ERROR|FATAL|PANIC" [$__auto]))',
                 "{{record_sql_state_code}}"),
    ], 12, 46, w=12,
        desc="SQLSTATE is the useful axis: 23505 unique violation, 40P01 deadlock, 53300 too many connections, 42P01 undefined table."))
    p.append(logs_panel(
        "Database log",
        '{app="lab-pg", container="postgres"} | json | logger="postgres" '
        '| line_format "{{.record_error_severity}} [{{.record_database_name}}] {{.record_message}}"',
        0, 53, h=12,
        desc="Every line PostgreSQL wrote. Open one for the full detail — session id, query, SQLSTATE, hint."))
    p.append(logs_panel(
        "Errors only",
        '{app="lab-pg", container="postgres"} | json | logger="postgres" '
        '| record_error_severity =~ "ERROR|FATAL|PANIC" '
        '| line_format "{{.record_error_severity}} [{{.record_sql_state_code}}] {{.record_message}}'
        '{{if .record_query}} — query: {{.record_query}}{{end}}"',
        0, 65, h=10,
        desc="Errors with the statement that caused them. This is usually the fastest route from an application incident to its cause."))
    p.append(logs_panel(
        "Slow queries",
        '{app="lab-pg", container="postgres"} | json | logger="postgres" '
        '| record_message =~ "(?i)duration:.*" '
        '| line_format "[{{.record_database_name}}] {{.record_message}}"',
        0, 75, h=8,
        desc="log_min_duration_statement is 1000ms, so anything appearing here took over a second."))
    p.append(logs_panel(
        "Operator log — failovers, switchovers, reconciliation",
        '{app="lab-pg"} | json | logger!="postgres"',
        0, 83, h=8,
        desc="CloudNativePG's own decisions, separate from anything PostgreSQL said."))

    return dashboard(
        "lab-postgres", "PostgreSQL",
        "The shared database: availability, connections, throughput, cache, storage and logs.",
        ["lab", "database", "postgres"], p,
        templating=[
            {"name": "ds_prom", "label": "Metrics source", "type": "datasource",
             "query": "prometheus", "current": {}, "hide": 0, "refresh": 1},
            {"name": "ds_loki", "label": "Logs source", "type": "datasource",
             "query": "loki", "current": {}, "hide": 0, "refresh": 1},
        ])




# ══════════════════════════════════════════════════════════════════════════
# 5. Trading agent — safety posture first, then decisions
# ══════════════════════════════════════════════════════════════════════════
# The chart's ServiceMonitor sets no jobLabel, so Prometheus uses the
# service name — not the namespace/name form a PodMonitor produces.
TA = 'job="trading-agent", namespace="demo"'
TA_LOGS = '{app="trading-agent", namespace="demo"}'


def trading_agent() -> dict:
    p = []

    # ── Safety ────────────────────────────────────────────────────────────
    # Deliberately the first row. The question that matters most about a
    # trading system is not how it is performing but what it is permitted to
    # do, and that should be answerable without scrolling.
    p.append(row("Safety posture — what the agent is permitted to do", 0))
    p.append(stat("Mode", 'count(trading_agent_trading_enabled) * 0 + 1', "short", 0, 1, w=4,
                  thresholds=steps(("green", None)),
                  desc="PAPER. Enforced in four places: startup config validation, the executor's endpoint guard, the risk engine's RequirePaperMode, and the credentials themselves — the paper keys are rejected by Alpaca's live endpoint."))
    p.append(stat("Order submission", f'max(trading_agent_trading_enabled{{{TA}}})', "short", 4, 1, w=4,
                  thresholds=steps(("blue", None), ("orange", 1)),
                  desc="1 = orders can be placed. 0 = the risk engine rejects everything with KILL_SWITCH. Orange is not an error; it means the agent is armed."))
    p.append(stat("Market", f'max(trading_agent_market_open{{{TA}}})', "short", 8, 1, w=4,
                  thresholds=steps(("text", None), ("green", 1)),
                  desc="1 when Alpaca reports the market open. Nothing is evaluated while it is closed."))
    p.append(stat("Agent up", f'max(up{{{TA}}})', "short", 12, 1, w=4,
                  thresholds=steps(("red", None), ("green", 1))))
    p.append(stat("Audit failures", f'sum(trading_agent_audit_failures_total{{{TA}}}) or vector(0)',
                  "short", 16, 1, w=4, decimals=0,
                  thresholds=steps(("green", None), ("red", 1)),
                  desc="Decisions that could not be written to the audit trail. Any value above zero means history is incomplete — and an order that cannot be recorded is refused rather than sent."))
    p.append(stat("Cycle failures (1h)",
                  f'sum(increase(trading_agent_cycles_total{{{TA}, result="failed"}}[1h])) or vector(0)',
                  "short", 20, 1, w=4, decimals=0,
                  thresholds=steps(("green", None), ("orange", 1)),
                  desc="A failed cycle places no orders. The loop continues, because a crash-looping agent tells an operator less than a running one reporting failures."))

    # ── Decisions ─────────────────────────────────────────────────────────
    p.append(row("Decisions", 5))
    p.append(ts("Outcomes per second", [
        tgt(f'sum by (outcome) (rate(trading_agent_evaluations_total{{{TA}}}[5m]))', "{{outcome}}"),
    ], "short", 0, 6, w=12, stack=True,
        desc="Every evaluation ends in one of these. kill_switch means the risk engine blocked it because trading is disabled; approved means an order was placed."))
    p.append({
        "type": "piechart", "title": "Outcome share (window)",
        "description": "What the agent has decided over the selected range. A healthy paper run is dominated by no_trade and kill_switch — approvals are rare by design.",
        "gridPos": {"h": 8, "w": 6, "x": 12, "y": 6},
        "datasource": {"type": "prometheus", "uid": PROM},
        "targets": [tgt(f'sum by (outcome) (increase(trading_agent_evaluations_total{{{TA}}}[$__range]))',
                        "{{outcome}}", instant=True)],
        "options": {"legend": {"displayMode": "list", "placement": "right", "showLegend": True},
                    "reduceOptions": {"calcs": ["lastNotNull"], "values": False}},
        "fieldConfig": {"defaults": {}, "overrides": []},
    })
    p.append(table("Outcomes by symbol", [
        tgt(f'sum by (symbol, outcome) (increase(trading_agent_evaluations_total{{{TA}}}[$__range]))',
            "", instant=True),
    ], 18, 6, w=6, h=8,
        desc="Which symbols are being rejected and why. A symbol stuck on one rejection code is usually a config mismatch rather than a market condition.",
        transforms=[{"id": "organize", "options": {"excludeByName": {
            "Time": True, "__name__": True, "container": True, "endpoint": True,
            "instance": True, "job": True, "namespace": True, "pod": True, "service": True}}}]))

    p.append(ts("Rejections by reason", [
        tgt(f'sum by (outcome) (increase(trading_agent_evaluations_total{{{TA}, outcome!="approved"}}[15m]))',
            "{{outcome}}"),
    ], "short", 0, 14, w=12,
        desc="no_data means market data was rejected as unusable — a stale quote, a wide spread, a missing bar. The agent holds rather than inferring a price."))
    p.append(ts("Evaluations by symbol", [
        tgt(f'sum by (symbol) (rate(trading_agent_evaluations_total{{{TA}}}[5m]))', "{{symbol}}"),
    ], "short", 12, 14, w=12))

    # ── Data feed ─────────────────────────────────────────────────────────
    # A thin feed does not produce wrong trades, it produces missing ones,
    # which is far harder to notice. These panels exist so that "the strategy
    # never fires" can be told apart from "the agent never saw a tradable
    # quote".
    p.append(row("Market data quality", 22))
    p.append(table("Quote rejections by symbol and reason", [
        tgt(f'sum by (symbol, reason) (increase(trading_agent_market_data_rejections_total{{{TA}}}[$__range]))',
            "", instant=True),
    ], 0, 23, w=12, h=8,
        desc="wide_spread concentrated on a few liquid symbols means the data feed is thin, not the market. "
             "IEX is one venue; when it has no size at the inside its quote reads far wider than the real "
             "market and the spread filter correctly refuses it. Switch ALPACA_DATA_FEED to sip to compare.",
        transforms=[{"id": "organize", "options": {"excludeByName": {
            "Time": True, "__name__": True, "container": True, "endpoint": True,
            "instance": True, "job": True, "namespace": True, "pod": True, "service": True}}}]))
    p.append(ts("Share of evaluations lost to unusable quotes", [
        tgt(f'sum(rate(trading_agent_market_data_rejections_total{{{TA}}}[15m])) '
            f'/ clamp_min(sum(rate(trading_agent_evaluations_total{{{TA}}}[15m])), 0.0001)', "rejected"),
    ], "percentunit", 12, 23, w=12, h=8,
        desc="Evaluations that never reached the strategy because the quote was not tradable. "
             "Sustained above a few percent on liquid names is a data-plan problem, not a market one. "
             "The wrong fix is raising maximumSpreadBps: that makes the agent trade on a quote it has "
             "already established is unreliable."))

    # ── Orders ────────────────────────────────────────────────────────────
    p.append(row("Orders", 31))
    p.append(stat("Approved (24h)",
                  f'sum(increase(trading_agent_evaluations_total{{{TA}, outcome="approved"}}[24h])) or vector(0)',
                  "short", 0, 32, w=6, decimals=0,
                  thresholds=steps(("text", None)),
                  desc="Orders the risk engine approved and sent to the broker. Bounded by the daily order limit in trading.json."))
    p.append(ts("Approvals over time", [
        tgt(f'sum(increase(trading_agent_evaluations_total{{{TA}, outcome="approved"}}[1h]))', "approved/hour"),
    ], "short", 6, 32, w=18, h=6,
        desc="Flat at zero is the expected picture for a conservative momentum strategy on five symbols."))

    # ── Health ────────────────────────────────────────────────────────────
    p.append(row("Health", 38))
    p.append(ts("Cycle duration", [
        tgt(f'histogram_quantile(0.95, sum by (le) (rate(trading_agent_cycle_duration_seconds_bucket{{{TA}}}[5m])))', "p95"),
        tgt(f'histogram_quantile(0.50, sum by (le) (rate(trading_agent_cycle_duration_seconds_bucket{{{TA}}}[5m])))', "p50", "B"),
    ], "s", 0, 39, w=8,
        desc="One cycle fetches the clock, calendar, account, orders, positions, then quotes and bars per symbol. Approaching the 30s interval means cycles would start overlapping."))
    p.append(ts("Cycles per second", [
        tgt(f'sum by (result) (rate(trading_agent_cycles_total{{{TA}}}[5m]))', "{{result}}"),
    ], "short", 8, 39, w=8))
    p.append(ts("Memory and CPU", [
        tgt('sum(container_memory_working_set_bytes{namespace="demo", pod=~"trading-agent-.*", container!=""})', "memory"),
    ], "bytes", 16, 39, w=8))

    # ── Logs ──────────────────────────────────────────────────────────────
    p.append(row("Decision log", 47))
    p.append(logs_panel(
        "Orders and warnings",
        f'{TA_LOGS} | json | severity =~ "WARNING|ERROR|CRITICAL"',
        0, 48, h=8,
        desc="Order submissions are logged at WARNING precisely so they surface here rather than being lost among routine holds."))
    p.append(logs_panel(
        "Every decision",
        f'{TA_LOGS} | json | logger =~ ".*TradingWorker.*" | line_format "{{{{.message}}}}"',
        0, 56, h=14,
        desc="One line per symbol per cycle: the outcome, the reason, and the strategy's confidence."))

    return dashboard(
        "lab-trading-agent", "Trading Agent",
        "Paper-trading agent: safety posture, decisions, orders and health.",
        ["lab", "application", "trading"], p,
        templating=[
            {"name": "ds_prom", "label": "Metrics source", "type": "datasource",
             "query": "prometheus", "current": {}, "hide": 0, "refresh": 1},
            {"name": "ds_loki", "label": "Logs source", "type": "datasource",
             "query": "loki", "current": {}, "hide": 0, "refresh": 1},
        ])


if __name__ == "__main__":
    print("generating dashboards:")
    write(cluster_overview(), "cluster-overview.json")
    write(fleet(), "fleet.json")
    write(platform_health(), "platform-health.json")
    write(postgres(), "postgres.json")
    write(trading_agent(), "trading-agent.json")

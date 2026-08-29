"""Turn Ingress objects into the app tiles shown on the page.

Ingresses are the right source: an Ingress exists precisely because someone
wanted the thing reachable, and it already carries the hostname, the TLS
setting and the backing Service. No separate registry to keep in sync.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

# Opt-out and per-app overrides, set through ingress.annotations in the
# app's values.yaml.
ANN_HIDE = "portal.sfy247.io/hide"
ANN_HEALTH_PATH = "portal.sfy247.io/health-path"
ANN_DESCRIPTION = "portal.sfy247.io/description"
ANN_ICON = "portal.sfy247.io/icon"


@dataclass(slots=True)
class App:
    # Set for apps that serve no HTTP to the outside world. They still get a
    # tile and a health check; they simply have nothing to link to.
    name: str
    namespace: str
    url: str
    host: str
    service: str
    service_port: int | str
    health_path: str
    description: str = ""
    icon: str = ""
    is_platform: bool = False
    internal: bool = False
    # filled in by the health checker
    status: str = "unknown"
    status_code: int | None = None
    latency_ms: float | None = None
    detail: str = ""
    labels: dict[str, str] = field(default_factory=dict)

    @property
    def probe_url(self) -> str:
        """In-cluster URL — bypasses ingress, so it tests the app itself."""
        return (
            f"http://{self.service}.{self.namespace}.svc.cluster.local"
            f":{self.service_port}{self.health_path}"
        )


def service_port_index(services: list[dict[str, Any]]) -> dict[tuple[str, str, str], int]:
    """(namespace, service, port-name) -> port number.

    Ingress backends may name their port instead of numbering it — the
    generic-app chart does exactly that — and a name cannot be used in a URL.
    """
    index: dict[tuple[str, str, str], int] = {}
    for svc in services:
        meta = svc.get("metadata", {})
        ns, name = meta.get("namespace", ""), meta.get("name", "")
        for port in svc.get("spec", {}).get("ports", []) or []:
            if port.get("name") and port.get("port"):
                index[(ns, name, port["name"])] = int(port["port"])
    return index


def _first_rule(ingress: dict[str, Any]) -> dict[str, Any] | None:
    rules = ingress.get("spec", {}).get("rules") or []
    return rules[0] if rules else None


def apps_from_ingresses(
    ingresses: list[dict[str, Any]],
    *,
    url_suffix: str,
    default_health_path: str,
    platform_namespaces: list[str],
    port_index: dict[tuple[str, str, str], int] | None = None,
) -> list[App]:
    apps: list[App] = []
    for ingress in ingresses:
        meta = ingress.get("metadata", {})
        annotations = meta.get("annotations", {}) or {}
        if annotations.get(ANN_HIDE, "").lower() == "true":
            continue

        rule = _first_rule(ingress)
        if not rule or not rule.get("host"):
            continue  # no hostname means nothing to link to

        paths = rule.get("http", {}).get("paths") or []
        if not paths:
            continue
        backend = paths[0].get("backend", {}).get("service", {})
        service_name = backend.get("name")
        if not service_name:
            continue
        port = backend.get("port", {})
        namespace = meta.get("namespace", "default")
        if port.get("number"):
            service_port: int = int(port["number"])
        elif port.get("name"):
            # Resolve the name via the Service; fall back to 80 rather than
            # producing a URL with a word where the port should be.
            service_port = (port_index or {}).get(
                (namespace, service_name, port["name"]), 80
            )
        else:
            service_port = 80

        host = rule["host"]
        scheme = "https" if ingress.get("spec", {}).get("tls") else "http"

        apps.append(
            App(
                name=meta.get("name", service_name),
                namespace=namespace,
                url=f"{scheme}://{host}{url_suffix}",
                host=host,
                service=service_name,
                service_port=service_port,
                health_path=annotations.get(ANN_HEALTH_PATH, default_health_path),
                description=annotations.get(ANN_DESCRIPTION, ""),
                icon=annotations.get(ANN_ICON, ""),
                is_platform=namespace in platform_namespaces,
                labels=meta.get("labels", {}) or {},
            )
        )
    return sorted(apps, key=lambda a: (a.is_platform, a.namespace, a.name))


def apps_from_services(
    services: list[dict[str, Any]],
    *,
    already_seen: set[tuple[str, str]],
    label_selector: str,
    default_health_path: str,
    platform_namespaces: list[str],
) -> list[App]:
    """Applications that have a Service but no Ingress — workers, mostly.

    They cannot be linked to, but "is it running" is still worth answering,
    and an overview that silently omits half the platform is misleading.
    """
    key, _, value = label_selector.partition("=")
    apps: list[App] = []

    for service in services:
        meta = service.get("metadata", {})
        namespace, name = meta.get("namespace", ""), meta.get("name", "")
        if (namespace, name) in already_seen:
            continue

        labels = meta.get("labels", {}) or {}
        if value and labels.get(key) != value:
            continue

        annotations = meta.get("annotations", {}) or {}
        if annotations.get(ANN_HIDE, "").lower() == "true":
            continue

        ports = service.get("spec", {}).get("ports") or []
        if not ports:
            continue
        port = ports[0].get("port")
        if not port:
            continue

        apps.append(
            App(
                name=labels.get("app.kubernetes.io/instance", name),
                namespace=namespace,
                url="",                      # nothing to link to
                host=f"{name}.{namespace}.svc.cluster.local",
                service=name,
                service_port=port,
                health_path=annotations.get(ANN_HEALTH_PATH, default_health_path),
                description=annotations.get(ANN_DESCRIPTION, ""),
                icon=annotations.get(ANN_ICON, ""),
                is_platform=namespace in platform_namespaces,
                internal=True,
                labels=labels,
            )
        )

    return apps

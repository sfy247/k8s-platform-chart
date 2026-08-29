"""Discovery is pure data transformation, so it tests without a cluster."""

from platform_portal.discovery import apps_from_ingresses

PLATFORM_NS = ["argocd", "observability"]


def ingress(name, ns, host, svc, port=80, tls=False, annotations=None):
    obj = {
        "metadata": {"name": name, "namespace": ns, "annotations": annotations or {}},
        "spec": {
            "rules": [
                {
                    "host": host,
                    "http": {"paths": [{"backend": {"service": {"name": svc, "port": {"number": port}}}}]},
                }
            ]
        },
    }
    if tls:
        obj["spec"]["tls"] = [{"hosts": [host]}]
    return obj


def build(items, url_suffix=":8090"):
    return apps_from_ingresses(
        items,
        url_suffix=url_suffix,
        default_health_path="/healthz",
        platform_namespaces=PLATFORM_NS,
    )


def test_builds_a_url_from_the_ingress_host() -> None:
    apps = build([ingress("api", "demo", "api.example.com", "api", 8080)])
    assert apps[0].url == "http://api.example.com:8090"
    assert apps[0].probe_url == "http://api.demo.svc.cluster.local:8080/healthz"


def test_tls_ingress_becomes_https() -> None:
    apps = build([ingress("api", "demo", "api.example.com", "api", tls=True)])
    assert apps[0].url.startswith("https://")


def test_no_url_suffix_for_a_real_load_balancer() -> None:
    # On EKS the ingress is on :443, so URL_SUFFIX is empty.
    apps = build([ingress("api", "demo", "api.example.com", "api", tls=True)], url_suffix="")
    assert apps[0].url == "https://api.example.com"


def test_platform_namespaces_are_separated() -> None:
    apps = build([
        ingress("grafana", "observability", "grafana.example.com", "grafana"),
        ingress("api", "demo", "api.example.com", "api"),
    ])
    by_name = {a.name: a for a in apps}
    assert by_name["grafana"].is_platform is True
    assert by_name["api"].is_platform is False


def test_annotations_override_defaults() -> None:
    apps = build([ingress("api", "demo", "api.example.com", "api", annotations={
        "portal.sfy247.io/health-path": "/readyz",
        "portal.sfy247.io/description": "the thing",
        "portal.sfy247.io/icon": "🚀",
    })])
    assert apps[0].health_path == "/readyz"
    assert apps[0].description == "the thing"
    assert apps[0].icon == "🚀"


def test_hide_annotation_removes_an_app() -> None:
    apps = build([ingress("secret", "demo", "s.example.com", "s", annotations={
        "portal.sfy247.io/hide": "true"})])
    assert apps == []


def test_ingress_without_a_host_is_skipped() -> None:
    bad = {"metadata": {"name": "x", "namespace": "demo"}, "spec": {"rules": []}}
    assert build([bad]) == []


def test_ingress_without_a_backend_service_is_skipped() -> None:
    bad = {
        "metadata": {"name": "x", "namespace": "demo"},
        "spec": {"rules": [{"host": "h.example.com", "http": {"paths": [{"backend": {}}]}}]},
    }
    assert build([bad]) == []


def test_named_backend_port_is_resolved_from_the_service() -> None:
    # The generic-app chart references its backend port by NAME, so this is
    # the normal case, not an edge case. An unresolved name would produce
    # http://svc:http/ which is not a valid URL.
    from platform_portal.discovery import service_port_index

    svc = {
        "metadata": {"name": "api", "namespace": "demo"},
        "spec": {"ports": [{"name": "http", "port": 8080}]},
    }
    ing = {
        "metadata": {"name": "api", "namespace": "demo", "annotations": {}},
        "spec": {"rules": [{"host": "api.example.com", "http": {"paths": [
            {"backend": {"service": {"name": "api", "port": {"name": "http"}}}}]}}]},
    }
    apps = apps_from_ingresses(
        [ing], url_suffix="", default_health_path="/healthz",
        platform_namespaces=[], port_index=service_port_index([svc]),
    )
    assert apps[0].probe_url == "http://api.demo.svc.cluster.local:8080/healthz"


def test_unresolvable_named_port_falls_back_to_80() -> None:
    ing = {
        "metadata": {"name": "api", "namespace": "demo", "annotations": {}},
        "spec": {"rules": [{"host": "api.example.com", "http": {"paths": [
            {"backend": {"service": {"name": "api", "port": {"name": "nope"}}}}]}}]},
    }
    apps = apps_from_ingresses(
        [ing], url_suffix="", default_health_path="/healthz",
        platform_namespaces=[], port_index={},
    )
    assert apps[0].service_port == 80


def svc(name, ns, port=80, labels=None, annotations=None):
    return {
        "metadata": {"name": name, "namespace": ns,
                     "labels": labels or {"app.kubernetes.io/name": "generic-app",
                                          "app.kubernetes.io/instance": name},
                     "annotations": annotations or {}},
        "spec": {"ports": [{"port": port, "name": "http"}]},
    }


def build_internal(services, seen=None):
    from platform_portal.discovery import apps_from_services
    return apps_from_services(
        services,
        already_seen=seen or set(),
        label_selector="app.kubernetes.io/name=generic-app",
        default_health_path="/healthz",
        platform_namespaces=PLATFORM_NS,
    )


def test_a_worker_with_no_ingress_still_appears() -> None:
    apps = build_internal([svc("trading-agent", "demo", 80)])
    assert len(apps) == 1
    assert apps[0].internal is True
    assert apps[0].url == ""          # nothing to link to
    assert apps[0].probe_url == "http://trading-agent.demo.svc.cluster.local:80/healthz"


def test_an_app_that_already_has_an_ingress_is_not_duplicated() -> None:
    seen = {("demo", "hello-python")}
    apps = build_internal([svc("hello-python", "demo")], seen=seen)
    assert apps == []


def test_services_without_the_platform_label_are_ignored() -> None:
    # Otherwise every Service in the cluster would become a tile.
    apps = build_internal([svc("kube-dns", "kube-system", labels={"k8s-app": "kube-dns"})])
    assert apps == []


def test_the_hide_annotation_works_for_workers_too() -> None:
    apps = build_internal([svc("secret-worker", "demo",
                               annotations={"portal.sfy247.io/hide": "true"})])
    assert apps == []


def test_a_service_with_no_ports_is_skipped() -> None:
    bad = {"metadata": {"name": "x", "namespace": "demo",
                        "labels": {"app.kubernetes.io/name": "generic-app"}, "annotations": {}},
           "spec": {"ports": []}}
    assert build_internal([bad]) == []


def test_an_app_that_hides_itself_does_not_reappear_via_its_service() -> None:
    from platform_portal.discovery import services_claimed_by_ingresses

    hidden = ingress("platform-portal", "demo", "portal.example.com", "platform-portal",
                     annotations={"portal.sfy247.io/hide": "true"})
    # It produces no tile from the Ingress...
    assert build([hidden]) == []
    # ...and must not come back through the Service path either.
    claimed = services_claimed_by_ingresses([hidden])
    assert build_internal([svc("platform-portal", "demo")], seen=claimed) == []

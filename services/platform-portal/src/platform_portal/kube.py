"""Minimal Kubernetes API client.

Deliberately hand-rolled rather than pulling in the full client library: the
portal makes exactly one kind of call (list Ingresses), and the in-cluster
auth story is short enough to be worth seeing.

Works unchanged on EKS — the ServiceAccount token and CA bundle are mounted
at the same paths in every conformant cluster.
"""

from __future__ import annotations

import logging
import os
from pathlib import Path
from typing import Any

import httpx

logger = logging.getLogger(__name__)

SA_DIR = Path("/var/run/secrets/kubernetes.io/serviceaccount")
TOKEN_PATH = SA_DIR / "token"
CA_PATH = SA_DIR / "ca.crt"
NAMESPACE_PATH = SA_DIR / "namespace"


class NotInCluster(RuntimeError):
    """Raised when the ServiceAccount credentials are not mounted."""


def in_cluster() -> bool:
    return TOKEN_PATH.exists()


def current_namespace(default: str = "default") -> str:
    try:
        return NAMESPACE_PATH.read_text().strip() or default
    except OSError:
        return default


class KubeClient:
    """Read-only client for the API server the pod is running in."""

    def __init__(self) -> None:
        if not in_cluster():
            raise NotInCluster(f"{TOKEN_PATH} not found — is automountToken enabled?")
        host = os.environ.get("KUBERNETES_SERVICE_HOST", "kubernetes.default.svc")
        port = os.environ.get("KUBERNETES_SERVICE_PORT", "443")
        self._base = f"https://{host}:{port}"
        # verify against the cluster CA; never disable this
        self._client = httpx.AsyncClient(
            base_url=self._base,
            verify=str(CA_PATH),
            timeout=httpx.Timeout(10.0),
        )

    @staticmethod
    def _token() -> str:
        # Re-read every call: projected ServiceAccount tokens are rotated,
        # and a token cached at startup silently expires after an hour.
        return TOKEN_PATH.read_text().strip()

    async def close(self) -> None:
        await self._client.aclose()

    async def _list(self, paths: list[str]) -> list[dict[str, Any]]:
        items: list[dict[str, Any]] = []
        headers = {"Authorization": f"Bearer {self._token()}"}
        for path in paths:
            response = await self._client.get(path, headers=headers)
            if response.status_code == 403:
                logger.warning("forbidden reading %s — check the ClusterRole rules", path)
                continue
            response.raise_for_status()
            items.extend(response.json().get("items", []))
        return items

    async def list_services(self, namespaces: list[str] | None = None) -> list[dict[str, Any]]:
        """Needed to resolve named Ingress backend ports to numbers.

        An Ingress may reference its backend port either by number or by
        name, and the name is only meaningful on the Service.
        """
        paths = (
            [f"/api/v1/namespaces/{ns}/services" for ns in namespaces]
            if namespaces
            else ["/api/v1/services"]
        )
        return await self._list(paths)

    async def list_ingresses(self, namespaces: list[str] | None = None) -> list[dict[str, Any]]:
        """Every Ingress the ServiceAccount is allowed to see."""
        paths = (
            [f"/apis/networking.k8s.io/v1/namespaces/{ns}/ingresses" for ns in namespaces]
            if namespaces
            else ["/apis/networking.k8s.io/v1/ingresses"]
        )
        return await self._list(paths)

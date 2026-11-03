#!/usr/bin/env python3
"""Reuse the existing real gateway fixture against a local measured image pair."""

import argparse
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
import time
from types import SimpleNamespace
import urllib.error


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/ci"))
spec = importlib.util.spec_from_file_location("telemetry_lifecycle", ROOT / "tools/ci/run-mcp-host-lifecycle-acceptance.py")
lifecycle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lifecycle)


def checked(arguments, timeout=60):
    return subprocess.check_output(arguments, timeout=timeout, text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-image", required=True)
    parser.add_argument("--web-image", required=True)
    parser.add_argument("--mcp-image", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    fixture = lifecycle.Fixture(SimpleNamespace(api_image=args.api_image, web_image=args.web_image,
                                               mcp_image=args.mcp_image, deployment_archive=None), existing=True)
    report = {"status": "RUNNING", "releaseEligible": False, "authenticationMode": "gateway",
              "fixture": "Existing lifecycle.Fixture prepare/probe_incident_search; synthetic disposable data",
              "externalAuthentikAcceptance": "NOT_RUN", "project": fixture.project,
              "executedConfigDigests": {}, "checks": {}}
    try:
        fixture.prepare()
        fixture.wait_console_endpoint()
        for service, image in (("api", args.api_image), ("web", args.web_image), ("mcp-http", args.mcp_image)):
            identifier = checked([*fixture.compose, "ps", "-q", service])
            observed = checked(["docker", "inspect", "--format", "{{.Image}}", identifier])
            expected = checked(["docker", "image", "inspect", "--format", "{{.Id}}", image])
            if observed != expected:
                raise RuntimeError("Fixture started a different image for " + service)
            report["executedConfigDigests"][service] = observed
            if service == "mcp-http":
                if checked(["docker", "exec", identifier, "id", "-u"]) != "10001":
                    raise RuntimeError("HTTP host lost its non-root user")
                deadline = time.monotonic() + 90
                while time.monotonic() < deadline:
                    health = checked(["docker", "inspect", "--format", "{{.State.Health.Status}}", identifier])
                    if health == "healthy":
                        break
                    if health == "unhealthy":
                        raise RuntimeError("HTTP host healthcheck failed")
                    time.sleep(0.5)
                else:
                    raise RuntimeError("HTTP host healthcheck readiness timed out")
        credential = fixture.request("/api/v1/integration-credentials/", {
            "name": "Disposable telemetry gateway acceptance", "purpose": "mcp",
            "organizationId": fixture.organization, "permissions": ["Incident.Read"],
            "lifetimeDays": 1, "mcpResourceUri": fixture.origin + "/mcp"}, expected=(201,))
        fixture.client_secret = credential["secret"]
        if credential["mcpResourceUri"] != fixture.origin + "/mcp":
            raise RuntimeError("Credential was not bound to the fixture MCP resource")
        fixture.probe_incident_search(secret=fixture.client_secret)
        report["checks"]["realInitializeCatalogAuthStatusSeededListFilterGetAndPermission403"] = "PASS"
        samples = []
        for _ in range(5):
            started = time.monotonic()
            fixture.probe_incident_search(secret=fixture.client_secret)
            samples.append(round((time.monotonic() - started) * 1000, 3))
        report["measurement"] = {"scope": "Five descriptive warm authenticated sessions, each initialize/catalog/seeded list/filter/get/permission denial; no performance improvement claim",
                                 "authenticatedReadWorkloadMilliseconds": samples, "memory": {}}
        for service in ("api", "web", "mcp-http"):
            identifier = checked([*fixture.compose, "ps", "-q", service])
            stats = json.loads(checked(["docker", "stats", "--no-stream", "--format", "{{json .}}", identifier]))
            report["measurement"]["memory"][service] = {"usage": stats["MemUsage"], "percent": stats["MemPerc"],
                "definition": "One post-workload docker stats Linux sample, cache excluded"}
        try:
            fixture.probe_incident_search(secret="rdk_synthetic_unauthorized_telemetry_fixture")
        except urllib.error.HTTPError as error:
            if error.code != 401:
                raise RuntimeError("Invalid credential did not return HTTP 401") from error
        else:
            raise RuntimeError("Invalid credential was accepted")
        report["checks"]["invalidCredential401"] = "PASS"
        fixture.request("/api/v1/integration-credentials/" + credential["id"], expected=(204,), method="DELETE")
        try:
            fixture.probe_incident_search(secret=fixture.client_secret)
        except urllib.error.HTTPError as error:
            if error.code != 401:
                raise RuntimeError("Revoked credential did not return HTTP 401") from error
        else:
            raise RuntimeError("Revoked credential was accepted")
        report["checks"]["revokedCredential401"] = "PASS"
        report["status"] = "PASS"
    except Exception as error:
        report["status"] = "FAIL"
        # The fixture records only redacted failures, command output and logs.
        report["errorType"] = type(error).__name__
        try:
            fixture.record_failure(error)
            failure = ROOT / "work/mcp-host-lifecycle" / fixture.project
            if failure.exists():
                shutil.copytree(failure, args.output.parent / (args.output.stem + "-failure"), dirs_exist_ok=True)
        except Exception as recording_error:
            report["failureEvidenceErrorType"] = type(recording_error).__name__
    finally:
        try:
            cleanup = subprocess.run([*fixture.compose, "down", "--volumes", "--remove-orphans"],
                                     capture_output=True, timeout=120, check=False)
            if cleanup.returncode:
                raise RuntimeError("Fixture Compose cleanup failed")
            for resource in ("container", "volume", "network"):
                command = ["docker", resource, "ls", "--filter", "label=com.docker.compose.project=" + fixture.project, "--quiet"]
                if resource == "container":
                    command.append("--all")
                remaining = checked(command)
                if remaining:
                    raise RuntimeError("Unique fixture resources remain after cleanup")
            report["cleanup"] = "PASS"
        except (RuntimeError, OSError, subprocess.SubprocessError):
            report.update(status="FAIL", cleanup="FAIL")
        finally:
            try:
                fixture.close()
            except Exception as close_error:
                report.update(status="FAIL", closeErrorType=type(close_error).__name__)
            finally:
                args.output.write_text(json.dumps(report, indent=2) + "\n")
    print("HTTP gateway fixture " + report["status"], flush=True)
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Opt-in read-only initialize probes at the deployed HTTP decoded-body boundary.

Uses the explicitly selected HTTPS MCP profile. Each route/framing/boundary gets
one request, with no retry or target fallback. Bodies contain valid initialize
JSON plus whitespace; no tools/call or business mutation is issued.
"""

import argparse
import hashlib
import http.client
import json
import re
from pathlib import Path
import sys
import time
from urllib.parse import urlsplit
import uuid

from campaign_reads import CampaignError, Captures, Transport, load_profile, utc


DEFAULT_LIMIT = 30_000_000
BOUNDARY_FORMATS = {"0.1.2-beta.11": "rateldesk-beta11-body-boundary/v1",
                    "0.1.2-beta.12": "rateldesk-beta12-body-boundary/v1"}


def boundary_format(manifest):
    version = manifest.get("version")
    if (version not in BOUNDARY_FORMATS or manifest.get("tag") != "v" + version
            or not isinstance(manifest.get("sourceRevision"), str)
            or not re.fullmatch(r"[0-9a-f]{40}", manifest["sourceRevision"])):
        raise CampaignError("Transport probes require an exact supported beta.11 or beta.12 release manifest")
    return BOUNDARY_FORMATS[version]


def initialize_body(size, rpc_id):
    message = {"jsonrpc": "2.0", "id": rpc_id, "method": "initialize", "params": {
        "protocolVersion": "2025-03-26", "capabilities": {},
        "clientInfo": {"name": "rateldesk-body-boundary", "version": "1"}}}
    raw = json.dumps(message, separators=(",", ":")).encode()
    if size < len(raw):
        raise CampaignError("Configured body limit is too small for a valid initialize control")
    return raw + b" " * (size - len(raw))


def decode_response(raw, rpc_id):
    if raw.lstrip().startswith(b"{"):
        return json.loads(raw)
    for line in raw.decode().splitlines():
        if line.startswith("data:"):
            value = json.loads(line[5:].strip())
            if value.get("id") == rpc_id:
                return value
    return {"nonJsonResponse": True}


def probe(resource, credential, path, framing, body, rpc_id):
    uri = urlsplit(resource)
    connection = http.client.HTTPSConnection(uri.hostname, uri.port or 443, timeout=60)
    headers = {"Authorization": "Bearer " + credential, "Content-Type": "application/json",
               "Accept": "application/json, text/event-stream", "MCP-Protocol-Version": "2025-03-26"}
    tick = time.monotonic()
    try:
        if framing == "chunked":
            headers["Transfer-Encoding"] = "chunked"
            chunks = (body[index:index + 16384] for index in range(0, len(body), 16384))
            connection.request("POST", path, body=chunks, headers=headers, encode_chunked=True)
        else:
            headers["Content-Length"] = str(len(body))
            connection.request("POST", path, body=body, headers=headers)
        response = connection.getresponse()
        raw = response.read(8 * 1024 * 1024 + 1)
        if len(raw) > 8 * 1024 * 1024:
            raise CampaignError("Transport probe response exceeds the capture bound")
        return {"observedAt": utc(), "httpStatus": response.status,
                "contentType": response.getheader("Content-Type"), "body": decode_response(raw, rpc_id),
                "latencyMs": (time.monotonic() - tick) * 1000}
    finally:
        connection.close()


def expected_result(response, size, limit, rpc_id):
    body = response.get("body", {})
    content_type = response.get("contentType")
    if size <= limit:
        return response.get("httpStatus") == 200 and body.get("id") == rpc_id and bool(body.get("result", {}).get("serverInfo"))
    return (response.get("httpStatus") == 413 and body.get("status") == 413
            and body.get("code") == "request_too_large" and body.get("limit") == "body"
            and body.get("maximumBodyBytes") == limit
            and isinstance(content_type, str)
            and content_type.split(";", 1)[0].strip().lower() == "application/problem+json")


def run(args):
    if type(args.limit) is not int or not 1 < args.limit <= DEFAULT_LIMIT:
        raise CampaignError("Use the observed decoded-body limit, no greater than 30000000 bytes")
    if args.output.resolve().is_relative_to(Path(__file__).resolve().parents[2]):
        raise CampaignError("Transport evidence must remain outside the checkout")
    profile, credential = load_profile(args.profile)
    Transport(profile["resourceUri"], credential, None)  # Exact resource and TLS validation.
    manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
    summary_format = boundary_format(manifest)
    evidence_root = (args.evidence_root or args.output).resolve()
    if not args.output.resolve().is_relative_to(evidence_root):
        raise CampaignError("Transport output must be inside the selected evidence root")
    prefix = args.output.resolve().relative_to(evidence_root)
    args.output.mkdir(mode=0o700, parents=False, exist_ok=False)
    captures = Captures(args.output, [credential])
    transport = Transport(profile["resourceUri"], credential, captures)
    def save(kind, value):
        reference = captures.write(kind, value)
        reference["path"] = (prefix / reference["path"]).as_posix()
        return reference
    def observe_identity():
        raw, _, at, _ = transport.request(transport.origin + "/api/v1/system/version", "GET", None,
                                         {"Accept": "application/json"})
        identity = json.loads(raw)
        reference = save("deployment-identity", {"observedAt": at, "body": identity})
        if identity.get("version") != manifest["version"] or identity.get("commitHash") != manifest["sourceRevision"]:
            raise CampaignError("Public API identity differs from the pinned release manifest")
        return reference
    summary = {"format": summary_format, "startedAt": utc(),
               "maximumBodyBytes": args.limit, "completed": False, "cases": [],
               "resourceUri": profile["resourceUri"], "version": manifest["version"],
               "sourceRevision": manifest["sourceRevision"],
               "scope": "Fresh transport supporting evidence; not full campaign acceptance"}
    cases = [(path, framing, size) for path in ("/mcp", "/mcp/")
             for framing in ("content-length", "chunked") for size in (args.limit - 1, args.limit, args.limit + 1)]
    if args.limit == DEFAULT_LIMIT:
        cases.append(("/mcp/", "chunked", 29_990_208))
    try:
        summary["identityBefore"] = observe_identity()
        for path, framing, size in cases:
            rpc_id = str(uuid.uuid4())
            body = initialize_body(size, rpc_id)
            request = {"observedAt": utc(), "path": path, "framing": framing,
                       "decodedBodyBytes": len(body), "bodySha256": hashlib.sha256(body).hexdigest(),
                       "uploadBehavior": "send-complete-body-before-reading-response",
                       "rpcId": rpc_id, "method": "initialize", "generator": "valid-json-plus-trailing-whitespace/v1"}
            request_ref = save("transport-request", request)
            try:
                response = probe(profile["resourceUri"], credential, path, framing, body, rpc_id)
                passed = expected_result(response, size, args.limit, rpc_id)
            except (OSError, http.client.HTTPException, ValueError, CampaignError) as error:
                response = {"observedAt": utc(), "transportErrorType": type(error).__name__}
                passed = False
            response_ref = save("transport-response", response)
            summary["cases"].append({"path": path, "framing": framing, "decodedBodyBytes": size,
                                     "request": request_ref, "response": response_ref, "passed": passed})
            print(f"{path} {framing} {size} bytes: {'PASS' if passed else 'FAIL'}", flush=True)
        summary["identityAfter"] = observe_identity()
        summary["completed"] = True
    finally:
        summary["completedAt"] = utc()
        reference = save("transport-summary", summary)
        print(json.dumps({"transportBoundary": reference}), flush=True)
    return 0 if all(case["passed"] for case in summary["cases"]) else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--evidence-root", type=Path)
    parser.add_argument("--limit", type=int, default=DEFAULT_LIMIT)
    try:
        raise SystemExit(run(parser.parse_args()))
    except Exception as error:
        print(str(error) if isinstance(error, CampaignError) else
              "Transport probes stopped (" + type(error).__name__ + "); inspect private captures.", file=sys.stderr)
        raise SystemExit(1)

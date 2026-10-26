#!/usr/bin/env python3
"""Opt-in deployed HTTP MCP reads, independently checked against the backend.

This produces private supporting captures, not a full campaign acceptance receipt.
No mutation, credential issuance, target fallback or ambient profile discovery.
"""

import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import sys
import time
from datetime import datetime, timezone
from urllib.error import HTTPError
from urllib.parse import quote, urlencode, urlsplit
from urllib.request import build_opener, HTTPRedirectHandler, Request
import uuid

from jsonschema import Draft202012Validator, FormatChecker


class CampaignError(Exception):
    """Safe diagnostic: never includes a response body or credential."""


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise CampaignError("Redirect refused; verify the explicitly selected endpoint")


def utc():
    return datetime.now(timezone.utc).isoformat()


def pointer(value, path):
    if path == "":
        return value
    if not path.startswith("/"):
        raise CampaignError("Assertion must use a JSON pointer")
    for part in path[1:].split("/"):
        part = part.replace("~1", "/").replace("~0", "~")
        value = value[int(part)] if isinstance(value, list) else value[part]
    return value


def read_operation(catalog, case, validate_arguments=True):
    rows = {x["tool"] + "." + x["operation"]: x for x in catalog["operations"]}
    op = rows[case["operation"]]
    if op["method"] != "GET" or op["readOnly"] is not True:
        raise CampaignError("This runner accepts only advertised GET/read-only operations")
    if op.get("sensitiveResultFields"):
        raise CampaignError("Declared secret-returning operations need a dedicated private lane")
    schema = next(x["inputSchema"] for x in catalog["toolSchemas"] if x["tool"] == op["tool"])
    args = {"operation": op["operation"]}
    if "request" in case:
        args["request"] = case["request"]
    if validate_arguments and not Draft202012Validator(schema, format_checker=FormatChecker()).is_valid(args):
        raise CampaignError("Plan arguments do not satisfy the selected catalog schema")
    if not case.get("assertions"):
        raise CampaignError("Every read needs fixture-specific typed assertions")
    path = op["pathTemplate"]
    query = {}
    for name, value in case.get("request", {}).items():
        selector = "{" + name + "}"
        if selector in path:
            path = path.replace(selector, quote(str(value), safe=""))
        else:
            if value is None:
                continue
            if isinstance(value, dict) or (isinstance(value, list) and
                    any(isinstance(item, (dict, list)) for item in value)):
                raise CampaignError("Nested query arguments require a dedicated read lane")
            if isinstance(value, bool):
                value = str(value).lower()
            query[name] = value
    if "{" in path or not path.startswith("/api/v1/"):
        raise CampaignError("Unresolved or unsupported independent API path")
    if any(part in (".", "..") for part in path.split("/")):
        raise CampaignError("Dot path selectors are not supported")
    return op, args, path + (("?" + urlencode(query, doseq=True)) if query else "")


def compare(mcp, api, assertions, sla_countdown_tolerance=0):
    if mcp.get("success") is not True or "data" not in mcp:
        raise CampaignError("MCP read did not succeed with structured data")
    if mcp["data"] in (None, [], {}):
        raise CampaignError("Empty observations do not establish positive fixture coverage")
    if type(sla_countdown_tolerance) is not int or not 0 <= sla_countdown_tolerance <= 5:
        raise CampaignError("SLA countdown tolerance must be an integer from zero to five seconds")
    left, right = copy.deepcopy(mcp["data"]), copy.deepcopy(api)
    if sla_countdown_tolerance:
        # Only these documented live countdowns may differ between sequential
        # reads. Every other field, including SLA state and deadlines, stays exact.
        for name in ("responseRemainingSeconds", "resolutionRemainingSeconds"):
            path = "/sla/" + name
            if any(check["pointer"] in ("", "/sla", path) for check in assertions):
                raise CampaignError("Use stable fixture assertions alongside an SLA countdown tolerance")
            before, after = pointer(left, path), pointer(right, path)
            if type(before) is not int or type(after) is not int or abs(before - after) > sla_countdown_tolerance:
                raise CampaignError("SLA countdown exceeded the explicitly bounded tolerance")
            left["sla"][name] = right["sla"][name]
    if left != right:
        raise CampaignError("MCP data differs from the independent API observation")
    for check in assertions:
        observed = pointer(api, check["pointer"])
        if type(observed) is not type(check["expected"]) or observed != check["expected"]:
            raise CampaignError("Typed fixture assertion failed")


def resolve_bindings(value, bindings):
    """Resolve exact references only from same-run independently verified reads."""
    if isinstance(value, dict):
        return {name: resolve_bindings(item, bindings) for name, item in value.items()}
    if isinstance(value, list):
        return [resolve_bindings(item, bindings) for item in value]
    if isinstance(value, str) and "${" in value:
        match = re.fullmatch(r"\$\{([A-Za-z][A-Za-z0-9_-]*\.[A-Za-z][A-Za-z0-9_-]*)\}", value)
        if not match or match[1] not in bindings:
            raise CampaignError("Fixture reference was not discovered and independently verified in this run")
        return bindings[match[1]]
    return value


def discovered_values(fixture, api):
    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]*", fixture.get("id", "")):
        raise CampaignError("Fixture needs a unique simple name")
    selectors = fixture.get("selectors")
    if not isinstance(selectors, dict) or not 1 <= len(selectors) <= 20:
        raise CampaignError("Fixture needs bounded explicit selector pointers")
    result = {}
    for name, path in selectors.items():
        if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]*", name):
            raise CampaignError("Fixture selector needs a simple name")
        value = pointer(api, path)
        if type(value) not in (str, int) or value == "":
            raise CampaignError("Discovered fixture selector must be a nonempty string or integer")
        result[fixture["id"] + "." + name] = value
    return result


def decode_api_read(path, raw, headers):
    # The workflow metrics API deliberately serves Prometheus; match the MCP
    # adapter's explicit representation without accepting arbitrary non-JSON.
    if path == "/api/v1/metrics/workflows":
        if headers.get("Content-Type", "").split(";", 1)[0].lower() != "text/plain":
            raise CampaignError("Workflow metrics did not return Prometheus text")
        return {"format": "prometheus", "text": raw.decode("utf-8")}
    return json.loads(raw)


class Captures:
    def __init__(self, directory, secrets=()):
        self.directory = directory
        self.secrets = list(secrets)
        self.sequence = 0

    def redact(self, value):
        protected = {"password", "newpassword", "currentpassword", "smtppassword", "token",
                     "secret", "signingsecret", "clientsecret", "accesstoken",
                     "refreshtoken", "plaintextkey", "apikey", "pairingcode", "pairingsecret",
                     "activationtoken", "integrationcredential", "authorization"}
        if isinstance(value, dict):
            return {k: "[REDACTED]" if k.casefold() in protected else self.redact(v)
                    for k, v in value.items()}
        if isinstance(value, list):
            return [self.redact(v) for v in value]
        if isinstance(value, str):
            # MCP duplicates structured content in JSON text blocks.
            if value.lstrip().startswith(("{", "[")):
                try:
                    return json.dumps(self.redact(json.loads(value)))
                except ValueError:
                    pass
            for secret in self.secrets:
                if secret:
                    value = value.replace(secret, "[REDACTED]")
            value = re.sub(r"(?i)([?&](?:amp;)?(?:token|key|code)=)[^&\s<>\"']+",
                           r"\1[REDACTED]", value)
        return value

    def write(self, kind, value):
        self.sequence += 1
        path = self.directory / f"{self.sequence:05d}-{kind}.json"
        raw = (json.dumps(self.redact(value), indent=2) + "\n").encode()
        path.write_bytes(raw)
        return {"path": path.name, "sha256": hashlib.sha256(raw).hexdigest()}


class Transport:
    def __init__(self, resource, credential, captures):
        uri = urlsplit(resource)
        if (uri.scheme != "https" or not uri.hostname or uri.username or uri.password
                or uri.path != "/mcp" or uri.query or uri.fragment):
            raise CampaignError("Select an explicit HTTPS origin with the exact /mcp resource")
        self.resource = resource
        self.origin = uri.scheme + "://" + uri.netloc
        self.credential = credential
        self.captures = captures
        self.opener = build_opener(NoRedirect())
        self.session = None
        self.last_call = 0

    def request(self, url, method, body, headers):
        started = utc()
        tick = time.monotonic()
        request = Request(url, body, headers, method=method)
        try:
            with self.opener.open(request, timeout=30) as response:
                raw = response.read(8 * 1024 * 1024 + 1)
                if len(raw) > 8 * 1024 * 1024:
                    raise CampaignError("Response exceeded the capture bound")
                return raw, response.headers, started, (time.monotonic() - tick) * 1000
        except HTTPError as error:
            raw = error.read(8 * 1024 * 1024 + 1)
            try:
                error_body = json.loads(raw[:8 * 1024 * 1024])
            except (ValueError, UnicodeError):
                error_body = {"nonJsonOrTruncated": True}
            self.captures.write("http-error", {"observedAt": utc(), "method": method,
                "httpStatus": error.code, "body": error_body,
                "responseExceededBound": len(raw) > 8 * 1024 * 1024})
            raise CampaignError(f"HTTP {error.code}; no automatic retry or target fallback") from None

    def rpc(self, method, params, notification=False):
        delay = 0.5 - (time.monotonic() - self.last_call)
        if delay > 0:
            time.sleep(delay)
        message = {"jsonrpc": "2.0", "method": method, "params": params}
        if not notification:
            message["id"] = str(uuid.uuid4())
        request_ref = self.captures.write("mcp-request", message)
        headers = {"Authorization": "Bearer " + self.credential,
                   "Content-Type": "application/json", "Accept": "application/json, text/event-stream",
                   "MCP-Protocol-Version": "2025-03-26"}
        if self.session:
            headers["Mcp-Session-Id"] = self.session
        raw, response_headers, started, elapsed = self.request(
            self.resource, "POST", json.dumps(message).encode(), headers)
        self.last_call = time.monotonic()
        self.session = response_headers.get("Mcp-Session-Id", self.session)
        if notification:
            result = {}
        elif raw.lstrip().startswith(b"{"):
            result = json.loads(raw)
        else:
            events = [json.loads(line[5:].strip()) for line in raw.decode().splitlines()
                      if line.startswith("data:")]
            result = next(x for x in events if x.get("id") == message["id"])
        if not notification and result.get("id") != message["id"]:
            raise CampaignError("MCP response ID did not match its request")
        return result, {"request": request_ref,
                        "response": self.captures.write("mcp-response", result),
                        "startedAt": started, "latencyMs": elapsed}

    def independent_api(self, path):
        raw, _, _, _ = self.request(self.origin + "/api/v1/mcp/execution-token", "POST", b"",
                                   {"Authorization": "Bearer " + self.credential,
                                    "X-RatelDesk-Mcp-Resource": self.resource})
        exchange = json.loads(raw)
        token = exchange["accessToken"]
        self.captures.secrets.append(token)
        self.captures.write("api-execution-exchange", exchange)
        raw, headers, at, _ = self.request(self.origin + path, "GET", None,
                                    {"Authorization": "Bearer " + token, "Accept": "application/json"})
        body = decode_api_read(path, raw, headers)
        return body, self.captures.write("api-independent", {"observedAt": at, "path": path, "body": body})


def load_profile(profile_path):
    repo = Path(__file__).resolve().parents[2]
    profile_path = profile_path.resolve()
    if profile_path.is_relative_to(repo):
        raise CampaignError("Profile and evidence must remain outside the source checkout")
    profile = json.loads(profile_path.read_text(encoding="utf-8-sig"))
    keypath = Path(profile["credentialFile"])
    keypath = keypath if keypath.is_absolute() else profile_path.parent / keypath
    if keypath.resolve().is_relative_to(repo):
        raise CampaignError("Credential file must be outside the checkout")
    with keypath.open("rb") as source:
        credential_bytes = source.read(16385)
    if len(credential_bytes) > 16384:
        raise CampaignError("Credential file exceeds the supported bound")
    credential = credential_bytes.decode("utf-8-sig").strip()
    if not credential or "\n" in credential or "\r" in credential:
        raise CampaignError("Credential file must contain one nonempty credential")
    return profile, credential


def run(args):
    repo = Path(__file__).resolve().parents[2]
    output = args.output.resolve()
    profile_path = args.profile.resolve()
    if output.is_relative_to(repo) or profile_path.is_relative_to(repo):
        raise CampaignError("Profile and evidence must remain outside the source checkout")
    catalog_bytes = args.catalog.read_bytes()
    catalog = json.loads(catalog_bytes)
    plan = json.loads(args.plan.read_text(encoding="utf-8"))
    # Validate the whole plan before issuing even an initialize call.
    fixtures = plan.get("fixtures", [])
    if not isinstance(fixtures, list) or len(fixtures) > 100:
        raise CampaignError("Fixture discovery must be a bounded list")
    prepared_fixtures = [read_operation(catalog, fixture) for fixture in fixtures]
    # Check operation safety before any network request. Arguments that reference
    # fixtures are schema-validated after their same-run discovery, before use.
    for case in plan["cases"]:
        read_operation(catalog, case, validate_arguments=not fixtures)
    ids = [case["id"] for case in plan["cases"]]
    fixture_ids = [fixture["id"] for fixture in fixtures]
    if not ids or len(ids) != len(set(ids)) or len(fixture_ids) != len(set(fixture_ids)):
        raise CampaignError("Use at least one case with unique case IDs")
    profile, credential = load_profile(profile_path)
    # Refuse existing output rather than silently mixing candidate runs.
    output.mkdir(mode=0o700, parents=False, exist_ok=False)
    captures = Captures(output, [credential])
    transport = Transport(profile["resourceUri"], credential, captures)
    summary = {"startedAt": utc(), "catalogSha256": hashlib.sha256(catalog_bytes).hexdigest(),
               "planSha256": hashlib.sha256(args.plan.read_bytes()).hexdigest(),
               "scope": "Selected read-only supporting evidence; not full campaign acceptance",
               "fixtures": [], "cases": [], "completed": False}
    try:
        initialized, _ = transport.rpc("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
            "clientInfo": {"name": "rateldesk-campaign-reads", "version": "1"}})
        if initialized.get("result", {}).get("protocolVersion") != "2025-03-26":
            raise CampaignError("MCP initialize did not negotiate the supported protocol")
        transport.rpc("notifications/initialized", {}, notification=True)
        live, _ = transport.rpc("tools/list", {})
        expected = {x["tool"]: x["inputSchema"] for x in catalog["toolSchemas"]}
        actual = {x["name"]: x["inputSchema"] for x in live.get("result", {}).get("tools", [])}
        if live.get("result", {}).get("nextCursor") or actual != expected:
            raise CampaignError("Live tools/schema differ from the supplied released catalog")
        bindings = {}
        for fixture, (op, arguments, path) in zip(fixtures, prepared_fixtures):
            wire, mcp_ref = transport.rpc("tools/call", {"name": op["tool"], "arguments": arguments})
            api, api_ref = transport.independent_api(path)
            compare(wire.get("result", {}).get("structuredContent", {}), api, fixture["assertions"],
                    fixture.get("slaCountdownToleranceSeconds", 0))
            selected = discovered_values(fixture, api)
            bindings.update(selected)
            summary["fixtures"].append({"id": fixture["id"], "operation": fixture["operation"],
                "selectors": selected, "mcp": mcp_ref, "independentApi": api_ref,
                "assertions": fixture["assertions"]})
        for unresolved in plan["cases"]:
            case = resolve_bindings(unresolved, bindings)
            op, arguments, path = read_operation(catalog, case)
            wire, mcp_ref = transport.rpc("tools/call", {"name": op["tool"], "arguments": arguments})
            result = wire.get("result", {}).get("structuredContent", {})
            api, api_ref = transport.independent_api(path)
            compare(result, api, case["assertions"], case.get("slaCountdownToleranceSeconds", 0))
            summary["cases"].append({"id": case["id"], "operation": case["operation"],
                                     "outcome": "PASS", "mcp": mcp_ref, "independentApi": api_ref,
                                     "assertions": case["assertions"],
                                     "slaCountdownToleranceSeconds": case.get("slaCountdownToleranceSeconds", 0)})
        summary["completed"] = True
    finally:
        summary["completedAt"] = utc()
        captures.write("read-summary", summary)
    print(f"Verified {len(summary['cases'])} selected MCP/API reads; supporting evidence only.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--plan", type=Path, required=True)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    try:
        run(parser.parse_args())
    except Exception as error:
        # Untrusted response/error text may contain secrets. Do not dump it.
        print(str(error) if isinstance(error, CampaignError) else
              "Read campaign stopped (" + type(error).__name__ + "); inspect private captures.", file=sys.stderr)
        sys.exit(1)

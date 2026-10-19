#!/usr/bin/env python3
"""Offline beta.8 campaign evidence gate. Never contacts or mutates a deployment.

Templates remain NOT_RUN. Passing this gate checks evidence completeness and
recorded assertions; it does not authenticate the provenance of supplied captures.
"""

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import sys
import xml.etree.ElementTree as ET

VERSION = "0.1.2-beta.8"
FORMAT = "rateldesk-beta8-campaign/v1"
CATALOG_REVISION = "2026-10-19.mcp-owner-operations-v23"
IMAGE_FAMILIES = {
    "api": "ghcr.io/bostontechnologies/rateldesk-api",
    "web": "ghcr.io/bostontechnologies/rateldesk-web",
    "mcp-http": "ghcr.io/bostontechnologies/rateldesk-mcp-http",
}
CONTRACT = Path(__file__).with_name("beta8-campaign-contract.json")
CONTRACT_HASH = "c4f1d65ee78135cfb94a64d94eda8a016f7abdba5abd4337fbb1996eac8dab83"
JSON_LIMIT = 8 * 1024 * 1024
FILE_LIMIT = 64 * 1024 * 1024
BUNDLE_LIMIT = 1024 * 1024 * 1024
HASH = re.compile(r"[0-9a-f]{64}\Z")
SOURCE = re.compile(r"[0-9a-f]{40}\Z")
KINDS = {"positive", "expected_negative", "preview", "implementation"}
SURFACES = {"api", "cli", "ui", "storage", "provider", "transport_capture", "mcp", "test_report"}
HTTP_EXCLUSIONS = {"helpdesk_config.set", "helpdesk_config.unset"}
LOCAL_EQUIVALENTS = {
    "helpdesk_admin_mutations.provision_user": {"helpdesk_tenant_admin.create_local_account"},
    "helpdesk_admin_mutations.customer_invite": {"helpdesk_tenant_admin.create_local_account", "helpdesk_tenant_admin.reset_customer_login"},
    "helpdesk_admin_mutations.customer_resend_invite": {"helpdesk_tenant_admin.reset_local_account", "helpdesk_tenant_admin.reset_customer_login"},
    "helpdesk_admin_mutations.customer_disable_login": {"helpdesk_tenant_admin.disable_local_account"},
    "helpdesk_admin_mutations.customer_sync_authentik": {"helpdesk_customers.auth_status", "helpdesk_tenant_admin.reset_customer_login"},
}
LOCAL_SOURCE_CASES = {("B6-N01", "external_shared_principal_binding"), ("B6-N01", "external_admin_without_ai_scheme")}
REQUIRED_BETA8_OPERATIONS = {"helpdesk_auth.revoke_self", "helpdesk_automation.approve_task", "helpdesk_automation.reject_task",
    "helpdesk_automation.create_form", "helpdesk_email_settings.save_outgoing", "helpdesk_mail.pending_actions",
    "helpdesk_datasets.upsert_rows", "helpdesk_tenant_admin.create_local_account", "helpdesk_tenant_admin.reset_local_account",
    "helpdesk_tenant_admin.disable_local_account", "helpdesk_tenant_admin.reset_customer_login",
    "helpdesk_connections.generate_pairing_code", "helpdesk_connections.pair",
    "helpdesk_automation.create_rule", "helpdesk_automation.update_rule", "helpdesk_automation.delete_rule",
    "helpdesk_tenant_admin.create_asset", "helpdesk_tenant_admin.update_asset", "helpdesk_tenant_admin.delete_asset",
    "helpdesk_tenant_admin.update_instance_branding", "helpdesk_tenant_admin.upload_branding_logo",
    "helpdesk_tenant_admin.update_background_worker_settings", "helpdesk_tickets.current_assignee",
    "helpdesk_tickets.filter_options", "helpdesk_changes.my_approval", "helpdesk_changes.review_approval",
    "helpdesk_incidents.relations", "helpdesk_incidents.create_relation", "helpdesk_incidents.delete_relation",
    "helpdesk_incidents.bulk_delete", "helpdesk_incidents.bulk_marketing_spam",
    "helpdesk_ai_assistant.dispatch_investigation", "helpdesk_ai_assistant.chat_open",
    "helpdesk_ai_assistant.chat_send", "helpdesk_ai_assistant.chat_reconcile"}


class Invalid(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise Invalid(message)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate JSON property")
        result[key] = value
    return result


class NativeNumber(str):
    """Preserve native JsonNode number spelling when recalculating export hashes."""


def read_json(path, native=False):
    require(path.is_file() and path.stat().st_size <= JSON_LIMIT, "missing or oversized JSON evidence")
    options = {"parse_int": NativeNumber, "parse_float": NativeNumber} if native else {}
    value = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object,
                      parse_constant=lambda _: (_ for _ in ()).throw(Invalid("non-finite JSON number")), **options)
    return value


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def native_hash(value):
    # Match JsonNode.ToJsonString's ordered compact JSON and JavaScriptEncoder.
    def encode(item):
        if isinstance(item, NativeNumber):
            return str(item)
        if isinstance(item, dict):
            return "{" + ",".join(encode(k) + ":" + encode(v) for k, v in item.items()) + "}"
        if isinstance(item, list):
            return "[" + ",".join(encode(v) for v in item) + "]"
        text = json.dumps(item, ensure_ascii=True, separators=(",", ":"), allow_nan=False)
        def escape(match):
            token = match.group(0)
            if token == '\\"':
                return "\\u0022"
            if token.startswith("\\u"):
                return "\\u" + token[2:].upper()
            return "\\u" + format(ord(token), "04X") if len(token) == 1 else token
        return re.sub(r'''\\(?:["\\bfnrt]|u[0-9a-f]{4})|[&'<>+`\x7f]''', escape, text)
    return hashlib.sha256(encode(value).encode()).hexdigest()


def timestamp(value):
    require(isinstance(value, str), "timestamp is required")
    try:
        result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise Invalid("invalid timestamp") from error
    require(result.tzinfo is not None, "timestamp must include timezone")
    return result


def index(rows, label):
    require(isinstance(rows, list) and len(rows) <= 10000, label + " must be a bounded list")
    result = {}
    for row in rows:
        require(isinstance(row, dict) and isinstance(row.get("id"), str), label + " row needs an id")
        require(row["id"] not in result, "duplicate " + label + " id")
        result[row["id"]] = row
    return result


def operation_ids(schema):
    if not isinstance(schema, dict):
        return set()
    selector = schema.get("properties", {}).get("operation", {})
    found = {selector["const"]} if isinstance(selector.get("const"), str) else set()
    found.update(selector.get("enum", []))
    for keyword in ("allOf", "oneOf", "anyOf"):
        for branch in schema.get(keyword, []):
            found.update(operation_ids(branch))
    return found


def contract():
    require(sha256(CONTRACT) == CONTRACT_HASH, "campaign contract differs from this validator's fixed requirements")
    return read_json(CONTRACT)


def export_catalog(path):
    native = read_json(path, native=True)
    data = read_json(path)
    require(data.get("catalogRevision") == CATALOG_REVISION, "catalog is not the beta.8 owner-operation revision")
    require(native_hash(native["operations"]) == data["catalogHash"], "semantic catalog hash mismatch")
    require(native_hash(native["toolSchemas"]) == data["schemaHash"], "aggregate schema hash mismatch")
    rows = {f"{row['tool']}.{row['operation']}": row for row in data["operations"]}
    require(len(rows) == len(data["operations"]) == data["operationCount"], "catalog has duplicate or omitted operations")
    discovered = {f"{tool['tool']}.{operation}" for tool in data["toolSchemas"] for operation in operation_ids(tool["inputSchema"])}
    require(discovered == set(rows), "exported schemas and operation catalog disagree")
    require(set(contract()["historicalCatalog"]["operationIds"]) <= set(rows), "beta.7 operation removed from beta.8 scope")
    require(REQUIRED_BETA8_OPERATIONS <= set(rows), "beta.8 owner capability missing from catalog")
    return data, rows


def pointer(document, path):
    require(isinstance(path, str) and path.startswith("/") and len(path) <= 500, "assertion needs a bounded JSON pointer")
    for token in path[1:].split("/"):
        token = token.replace("~1", "/").replace("~0", "~")
        if isinstance(document, list):
            require(token.isdecimal() and int(token) < len(document), "assertion pointer misses array item")
            document = document[int(token)]
        else:
            require(isinstance(document, dict) and token in document, "assertion pointer misses observation field")
            document = document[token]
    return document


def tool_response(document):
    result = document.get("result", document)
    structured = result.get("structuredContent") if isinstance(result, dict) else None
    if isinstance(structured, dict):
        return structured
    if isinstance(result, dict) and "success" in result:
        return result
    if isinstance(document.get("httpStatus"), int) and document["httpStatus"] >= 400:
        return {"success": False, "status": "http_denial"}
    if "error" in document:
        return {"success": False, "status": "rpc_error"}
    raise Invalid("response lacks a structured MCP outcome")


class Validator:
    def __init__(self, receipt, evidence_root, catalog_path, manifest_path, now=None):
        self.data = receipt
        self.root = evidence_root.resolve()
        self.spec = contract()
        self.catalog, self.operations = export_catalog(catalog_path)
        self.catalog_file_hash = sha256(catalog_path)
        self.manifest = read_json(manifest_path)
        self.manifest_hash = sha256(manifest_path)
        self.now = now or datetime.now(timezone.utc)
        self.files, self.case_cache, self.bytes = {}, {}, 0

    def evidence(self, reference, as_json=True):
        require(isinstance(reference, dict) and set(reference) == {"path", "sha256"}, "evidence reference needs exactly path and sha256")
        name, digest = reference["path"], reference["sha256"]
        require(isinstance(name, str) and 0 < len(name) <= 300 and "\\" not in name and ":" not in name, "unsafe evidence path")
        relative = PurePosixPath(name)
        require(not relative.is_absolute() and ".." not in relative.parts, "evidence path escapes root")
        path = self.root.joinpath(*relative.parts)
        require(path.resolve().is_relative_to(self.root) and not any(self.root.joinpath(*relative.parts[:i]).is_symlink() for i in range(1, len(relative.parts) + 1)), "symlink evidence is unsupported")
        require(isinstance(digest, str) and HASH.fullmatch(digest), "evidence SHA256 is required")
        if name not in self.files:
            require(path.is_file() and 0 < path.stat().st_size <= FILE_LIMIT, "missing, empty or oversized evidence")
            require(len(self.files) < 20000, "too many evidence files")
            self.bytes += path.stat().st_size
            require(self.bytes <= BUNDLE_LIMIT, "evidence exceeds 1 GiB validation bound")
            self.files[name] = sha256(path)
        require(self.files[name] == digest, "evidence SHA256 mismatch")
        return read_json(path) if as_json else path

    def fresh(self, value):
        time = timestamp(value)
        require(self.started <= time <= self.completed, "observation is outside the fresh campaign")
        return time

    def assertions(self, observation, forbidden_hashes=()):
        require(observation.get("independent") is True, "independent readback is required")
        self.fresh(observation.get("observedAt"))
        rows = index(observation.get("assertions"), "assertion")
        require(0 < len(rows) <= 100, "at least one bounded postcondition assertion is required")
        surfaces = set()
        for row in rows.values():
            require(row.get("surface") in SURFACES, "unsupported postcondition surface")
            reference = row.get("evidence")
            actual = self.evidence(reference)
            require(reference["sha256"] not in forbidden_hashes, "postcondition must be captured independently of the tool request/response")
            require("expected" in row and json.dumps(pointer(actual, row.get("pointer")), sort_keys=True) == json.dumps(row["expected"], sort_keys=True), "recorded postcondition does not equal expected value")
            surfaces.add(row["surface"])
        for item in observation.get("supportingEvidence", []):
            require(item.get("surface") in SURFACES, "unsupported supporting surface")
            self.evidence(item.get("evidence"), as_json=False)
            surfaces.add(item["surface"])
        return set(rows), surfaces

    def provenance(self):
        require(self.data.get("format") == FORMAT and self.data.get("readiness") == "PASS", "campaign is not a completed PASS receipt")
        campaign = self.data["campaign"]
        self.started, self.completed = timestamp(campaign["startedAt"]), timestamp(campaign["completedAt"])
        require(self.started <= self.completed <= self.now, "campaign timestamps are unordered or in the future")
        self.functional = self.fresh(campaign["functionalCompletedAt"])
        self.mode = campaign.get("authenticationMode")
        require(self.mode in {"Local", "Authentik"}, "authentication mode must be observed Local or Authentik")
        require(campaign.get("version") == self.manifest.get("version") == VERSION, "candidate version must be exactly beta.8")
        require(campaign.get("tag") == self.manifest.get("tag") == "v" + VERSION, "candidate tag mismatch")
        source = campaign.get("sourceRevision")
        require(isinstance(source, str) and SOURCE.fullmatch(source) and source == self.manifest.get("sourceRevision"), "candidate source revision mismatch")
        require(self.data["releaseManifest"]["sha256"] == self.manifest_hash, "release manifest differs from pinned input")
        require(self.evidence(self.data["releaseManifest"]) == self.manifest, "release manifest contents differ")
        deployment = self.evidence(self.data["deployment"])
        self.fresh(deployment.get("observedAt"))
        require(deployment.get("authenticationMode") == self.mode, "deployed identity mode differs from receipt")
        mode_observation = self.evidence(deployment.get("authenticationModeEvidence"))
        require(mode_observation.get("authenticationMode") == self.mode, "authentication mode was not observed")
        require(set(deployment.get("components", {})) == {"api", "web", "mcp-http"}, "all three deployed components are required")
        images = {row["image"]: row["digest"] for row in self.manifest["containers"]}
        for name, component in deployment["components"].items():
            expected = images.get(f"{IMAGE_FAMILIES[name]}:{VERSION}")
            require(isinstance(expected, str) and expected.startswith("sha256:") and HASH.fullmatch(expected[7:]), "manifest lacks immutable component digest")
            require(component.get("digest") == expected and component.get("version") == VERSION and component.get("sourceRevision") == source, "deployed component identity mismatch")
            observation = self.evidence(component.get("observation"))
            require(observation.get("version") == VERSION and observation.get("sourceRevision") == source and observation.get("digest") == expected, "component capture contradicts declared identity")
        for tool in ("cli", "mcp-stdio"):
            native = deployment.get("nativeTools", {}).get(tool, {})
            name = native.get("asset", "")
            require(name.startswith(f"rateldesk-{tool}-{VERSION}-") and native.get("sha256") == self.manifest["assets"].get(name), "native archive is not a checksummed release asset")
            require(native.get("version") == VERSION and native.get("sourceRevision") == source, "native tool identity mismatch")
            observation = self.evidence(native.get("observation"))
            require(observation.get("version") == VERSION and observation.get("sourceRevision") == source and observation.get("archiveSha256") == native["sha256"], "native capture contradicts declared identity")
        observed = self.data["catalog"]
        require(observed.get("catalogHash") == self.catalog["catalogHash"] and observed.get("schemaHash") == self.catalog["schemaHash"], "live catalog/schema identity mismatch")
        require(observed["export"]["sha256"] == self.catalog_file_hash, "observed catalog differs from pinned release catalog")
        self.evidence(observed["export"])
        wire = self.evidence(observed["wireDiscovery"])
        tools = wire.get("result", wire).get("tools", [])
        wire_schemas = {item["name"]: item["inputSchema"] for item in tools}
        require(len(wire_schemas) == len(tools), "duplicate live tools")
        require(wire_schemas == {item["tool"]: item["inputSchema"] for item in self.catalog["toolSchemas"]}, "live discovery does not match released schemas")
        capabilities = tool_response(self.evidence(observed["capabilities"]))
        caps = capabilities.get("data", {})
        require(capabilities.get("success") is True and caps.get("catalogHash") == self.catalog["catalogHash"], "live capability hash was not independently observed")
        self.context = {"campaignId": campaign["id"], "version": VERSION, "tag": "v" + VERSION, "sourceRevision": source,
                        "deploymentSha256": self.data["deployment"]["sha256"], "catalogFileSha256": self.catalog_file_hash,
                        "catalogHash": self.catalog["catalogHash"], "schemaHash": self.catalog["schemaHash"]}
        require(isinstance(campaign["id"], str) and campaign["id"].strip(), "campaign id is required")
        require(self.data.get("productBlockers") == [] and self.data.get("unresolvedFailures") == 0, "product blockers or unresolved failures remain")

    def identities(self):
        self.actors = index(self.data.get("actors"), "actor")
        require(any(row.get("kind") == "owner" for row in self.actors.values()), "observed owner grant is required")
        require(any(row.get("kind") == "scoped" for row in self.actors.values()), "ordinary scoped identity is required")
        for row in self.actors.values():
            self.fresh(row.get("observedAt"))
            response = tool_response(self.evidence(row.get("accessEvidence")))
            if row.get("kind") in {"owner", "scoped"}:
                access = response.get("data", {})
                require(response.get("success") is True and access.get("isAuthenticated") is True, "actor access was not observed")
                if row["kind"] == "owner":
                    require(access.get("isHelpdeskAdmin") is True and access.get("grantKind") == "owner-administration", "owner grant must be explicit and currently authoritative")
                else:
                    require(access.get("isHelpdeskAdmin") is False and access.get("grantKind") == "ordinary", "ordinary credential unexpectedly has administrator authority")

    def case(self, identity, kind=None, operation=None):
        require(isinstance(identity, str) and identity in self.data["cases"], "missing case receipt")
        if identity not in self.case_cache:
            row = self.evidence(self.data["cases"][identity])
            require(row.get("context") == self.context and row.get("id") == identity, "case is bound to another campaign/source/catalog")
            require(row.get("outcome") == "PASS" and row.get("kind") in KINDS, "case is not a supported PASS outcome")
            start, end = self.fresh(row.get("startedAt")), self.fresh(row.get("completedAt"))
            require(start <= end <= self.functional, "case timing exceeds the functional campaign window")
            if row["kind"] == "implementation":
                require(row.get("origin") == "candidate_ci", "implementation case must retain CI classification")
                report = ET.parse(self.evidence(row.get("testResults"), as_json=False)).getroot()
                results = report.findall(".//{*}UnitTestResult")
                require(results and all(item.get("outcome") == "Passed" for item in results), "source test report has failed/skipped or no tests")
                row["verifiedAssertions"] = set(row.get("assertionIds", []))
                row["verifiedSurfaces"] = {"test_report"}
            else:
                require(row.get("origin") == "fresh_deployed" and row.get("transport") == "http", "positive coverage must use fresh deployed HTTP-MCP")
                require(row.get("actor") in self.actors and row.get("operation") in self.operations, "case actor or operation is unknown")
                request, response = self.evidence(row.get("request")), self.evidence(row.get("response"))
                params = request.get("params", {})
                arguments = params.get("arguments", {})
                require(request.get("method") == "tools/call" and params.get("name") + "." + arguments.get("operation", "") == row["operation"], "wire request does not invoke the claimed operation")
                if "id" in response:
                    require(request.get("id") == response["id"], "request/response RPC ids differ")
                result = tool_response(response)
                row["wireStatus"] = result.get("status")
                row["wireHttpStatus"] = response.get("httpStatus")
                if row["kind"] == "positive":
                    require(result.get("success") is True and result.get("status") != "confirmation_required", "denial or preview cannot count as a positive")
                    data = result.get("data")
                    if isinstance(data, dict):
                        require(data.get("success") is not False, "domain failure cannot count as a positive")
                        require(not any(data.get(key) == 0 for key in ("updated", "deleted", "affectedCount") if key in data), "zero-effect mutation cannot count as a positive")
                    if self.operations[row["operation"]]["confirmationRequired"]:
                        require(arguments.get("confirm") is True and row.get("effectObserved") is True, "positive mutation needs confirmed execution and observed effect")
                elif row["kind"] == "preview":
                    require(arguments.get("confirm") is not True and result.get("success") is False and result.get("status") == "confirmation_required", "preview must be an unconfirmed non-execution")
                    require(row.get("effectObserved") is False, "preview changed state")
                else:
                    require(result.get("success") is False and result.get("status") != "confirmation_required", "negative receipt did not demonstrate a denial")
                    require(row.get("effectObserved") is False, "denied operation changed state")
                names, surfaces = self.assertions(row["postcondition"], (row["request"]["sha256"], row["response"]["sha256"]))
                require(start <= timestamp(row["postcondition"]["observedAt"]) <= end, "postcondition is outside its case interval")
                row["verifiedAssertions"], row["verifiedSurfaces"] = names, surfaces
            self.case_cache[identity] = row
        result = self.case_cache[identity]
        require(kind is None or result["kind"] == kind, "case kind differs from required assertion")
        require(operation is None or result.get("operation") == operation, "case invokes a different operation")
        return result

    def operation_coverage(self):
        rows = index(self.data.get("operations"), "operation")
        require(set(rows) == set(self.operations), "every current operation must be accounted for exactly once")
        excluded = set()
        for identity, row in rows.items():
            if identity in HTTP_EXCLUSIONS or self.mode == "Local" and identity in LOCAL_EQUIVALENTS:
                require(row.get("state") == "EXCLUDED", "inapplicable capability must be separate from positive coverage")
                guard = self.case(row.get("guardCaseId"), "expected_negative", identity)
                require("explicit_capability_guard" in guard["verifiedAssertions"], "exclusion lacks observed capability guard")
                if identity in LOCAL_EQUIVALENTS:
                    equivalent = self.case(row.get("equivalentCaseId"), "positive")
                    require(equivalent["operation"] in LOCAL_EQUIVALENTS[identity], "Local exclusion lacks its fixed positive equivalent")
                excluded.add(identity)
                continue
            require(row.get("state") == "PASS", "product operation is blocked, omitted or arbitrarily excluded")
            positive = self.case(row.get("positiveCaseId"), "positive", identity)
            if self.operations[identity]["confirmationRequired"]:
                self.case(row.get("previewCaseId"), "preview", identity)
            if self.operations[identity].get("boundedIntegrationCredential", {}).get("support") == "denied_by_policy":
                require(self.actors[positive["actor"]].get("kind") == "owner", "administration positive must use the observed owner grant")
                denial = self.case(row.get("denialCaseId"), "expected_negative", identity)
                require(self.actors[denial["actor"]].get("kind") == "scoped", "ordinary credential denial is required for administrator-only operations")
                require(denial.get("wireStatus") in {"forbidden", "unauthorized"} or denial.get("wireHttpStatus") in {401, 403}, "administrator boundary needs actual authorization denial, not invalid input")
        return len(rows) - len(excluded), sorted(excluded)

    def scenarios_and_history(self):
        scenarios = index(self.data.get("scenarios"), "scenario")
        require(set(scenarios) == {row["id"] for row in self.spec["scenarios"]}, "required scenario family is missing")
        for requirement in self.spec["scenarios"]:
            cases = scenarios[requirement["id"]].get("cases", {})
            require(set(cases) == set(requirement["cases"]), "required domain scenario case is missing")
            for name, spec in requirement["cases"].items():
                case = self.case(cases[name], spec["kind"])
                require(spec["assertion"] in case["verifiedAssertions"], "domain-specific postcondition is missing")
                require(set(spec["surfaces"]) <= case["verifiedSurfaces"], "independent domain evidence surface is missing")
        historic = index(self.data.get("historicalObligations"), "historical obligation")
        require(set(historic) == {row["id"] for row in self.spec["historicalObligations"]}, "all 86 substantive historical obligations must be mapped")
        for obligation in self.spec["historicalObligations"]:
            cases = historic[obligation["id"]].get("cases", {})
            require(set(cases) == set(obligation["cases"]), "historical substantive case omitted")
            for name, kind in obligation["cases"].items():
                operation = obligation["operation"]
                if self.mode == "Local" and (obligation["id"], name) in LOCAL_SOURCE_CASES:
                    kind, operation = "implementation", None
                case = self.case(cases[name], kind)
                if self.mode == "Local" and operation in LOCAL_EQUIVALENTS:
                    require(case.get("operation") in LOCAL_EQUIVALENTS[operation], "historical external operation lacks fixed Local equivalent")
                elif operation is not None:
                    require(case.get("operation") == operation, "historical case operation changed")
                require(f"historical:{obligation['id']}/{name}" in case["verifiedAssertions"], "historical assertion lacks explicit evidence mapping")

    def soak_and_cleanup(self):
        soak = self.evidence(self.data.get("soak"))
        require(soak.get("context") == self.context, "soak context mismatch")
        start, end = self.fresh(soak.get("startedAt")), self.fresh(soak.get("completedAt"))
        require(self.functional <= start and 900 <= (end - start).total_seconds() <= 3600, "soak must follow functional work and last 900–3600 seconds")
        samples = self.evidence(soak.get("samples"))
        require(isinstance(samples, list) and 30 <= len(samples) <= 10000, "soak needs 30–10000 bounded samples")
        require(soak.get("totalRequests") == len(samples) and soak.get("unexpectedFailures") == 0, "soak totals or failures are inconsistent")
        times, latencies, captures = [], [], set()
        for sample in samples:
            at = timestamp(sample["at"])
            require(start <= at <= end, "soak sample lies outside run")
            require(sample.get("operation") in self.operations and self.operations[sample["operation"]]["readOnly"] is True, "soak must use declared read operations")
            response_ref = sample.get("response")
            require(sample.get("unexpectedFailure") is False and tool_response(self.evidence(response_ref)).get("success") is True, "soak contains failed requests")
            require(response_ref["sha256"] not in captures, "soak reuses a previous response capture")
            captures.add(response_ref["sha256"])
            latency = sample.get("latencyMs")
            require(type(latency) in {int, float} and 0 <= latency <= 60000, "invalid soak latency")
            times.append(at)
            latencies.append(latency)
        require(times == sorted(times) and (times[0] - start).total_seconds() <= 60 and (end - times[-1]).total_seconds() <= 60 and
                all((right - left).total_seconds() <= 60 for left, right in zip(times, times[1:])), "soak sampling has an unobserved gap")
        budget = self.evidence(soak.get("budget"))
        require(timestamp(budget["declaredAt"]) <= self.started and budget.get("metric") == "p95" and type(budget.get("limitMs")) in {int, float} and budget["limitMs"] > 0, "latency budget must be declared before the campaign")
        p95 = sorted(latencies)[max(0, (95 * len(latencies) + 99) // 100 - 1)]
        require(p95 <= budget["limitMs"], "observed soak p95 exceeds the declared budget")
        cleanup = self.evidence(self.data.get("cleanup"))
        require(cleanup.get("context") == self.context and self.fresh(cleanup.get("observedAt")) >= end, "cleanup must belong to and follow this campaign")
        assertions, _ = self.assertions(cleanup)
        require(set(self.spec["cleanupAssertions"]) <= assertions, "cleanup or uncertainty reconciliation is incomplete")
        return (end - start).total_seconds(), p95

    def validate(self):
        self.provenance()
        self.identities()
        require(isinstance(self.data.get("cases"), dict) and 0 < len(self.data["cases"]) <= 10000, "case registry must be bounded and nonempty")
        passed, excluded = self.operation_coverage()
        self.scenarios_and_history()
        duration, p95 = self.soak_and_cleanup()
        for identity in self.data["cases"]:
            self.case(identity)
        return {"readiness": "PASS", "version": VERSION, "authenticationMode": self.mode,
                "scope": self.mode + " owner HTTP-MCP capability campaign", "allAuthenticationModesValidated": False,
                "operationsPassed": passed, "operationsExcluded": excluded, "productBlockers": 0,
                "historicalObligations": 86, "scenarioCases": sum(len(row["cases"]) for row in self.spec["scenarios"]),
                "soakSeconds": duration, "soakP95Ms": p95, "validatedEvidenceFiles": len(self.files)}


def template(catalog_path):
    catalog, operations = export_catalog(catalog_path)
    spec = contract()
    return {"format": FORMAT, "readiness": "NOT_RUN",
            "campaign": {"id": None, "version": VERSION, "tag": "v" + VERSION, "sourceRevision": None,
                         "authenticationMode": None, "startedAt": None, "functionalCompletedAt": None, "completedAt": None},
            "releaseManifest": None, "deployment": None,
            "catalog": {"export": None, "wireDiscovery": None, "capabilities": None, "catalogHash": catalog["catalogHash"], "schemaHash": catalog["schemaHash"]},
            "actors": [], "cases": {},
            "operations": [{"id": identity, "state": "NOT_RUN", "positiveCaseId": None, "previewCaseId": None, "denialCaseId": None,
                            "guardCaseId": None, "equivalentCaseId": None} for identity in sorted(operations)],
            "scenarios": [{"id": row["id"], "cases": dict.fromkeys(row["cases"])} for row in spec["scenarios"]],
            "historicalObligations": [{"id": row["id"], "cases": dict.fromkeys(row["cases"])} for row in spec["historicalObligations"]],
            "soak": None, "cleanup": None, "productBlockers": [], "unresolvedFailures": None}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("template", "validate"))
    parser.add_argument("--catalog", required=True, type=Path, help="Exact beta.8 catalog exported from the released source")
    parser.add_argument("--manifest", type=Path, help="Independently downloaded immutable release manifest")
    parser.add_argument("--receipt", type=Path)
    parser.add_argument("--evidence-root", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    try:
        if args.mode == "template":
            require(args.output is not None, "template requires --output")
            require(not args.output.exists(), "refusing to overwrite an existing campaign receipt")
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(template(args.catalog), indent=2) + "\n", encoding="utf-8")
            print("NOT_RUN template created; no live evidence or production pass is implied.")
        else:
            require(args.manifest is not None and args.receipt is not None and args.evidence_root is not None,
                    "validate requires --manifest, --receipt and --evidence-root")
            result = Validator(read_json(args.receipt), args.evidence_root, args.catalog, args.manifest).validate()
            print(json.dumps(result, indent=2))
        return 0
    except (Invalid, OSError, ValueError, KeyError, TypeError, IndexError, ET.ParseError, RecursionError) as error:
        print("NOT_READY: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

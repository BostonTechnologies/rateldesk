#!/usr/bin/env python3
"""Beta.11 evidence gate and private campaign workspace.

The beta.8 validator and contract remain immutable. This release adapter reuses
their strict provenance, operation, domain, history, soak and cleanup checks with
explicit beta.11 identity bindings. Offline validation cannot authenticate a
capture's provenance and never performs a live operation.
"""

import argparse
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import sys


VERSION = "0.1.2-beta.11"
FORMAT = "rateldesk-beta11-campaign/v1"
CATALOG_REVISION = "2026-10-25.beta11-contracts-v25"
BASELINE = Path(__file__).with_name("beta10-operation-ids.json")
BASELINE_HASH = "4d3c42a3a95c336962f7fbe3630328bd6bcf0345c6004f7041deb9ff626e48a3"

# A separate module instance leaves consumers of the historical validator and
# every previous receipt unchanged. Only release identity parameters are adapted.
_spec = importlib.util.spec_from_file_location(
    "rateldesk_beta11_inherited_gate", Path(__file__).with_name("validate-beta8-campaign.py"))
legacy = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(legacy)
legacy.VERSION, legacy.FORMAT, legacy.CATALOG_REVISION = VERSION, FORMAT, CATALOG_REVISION
_export_catalog = legacy.export_catalog
Invalid, require, read_json = legacy.Invalid, legacy.require, legacy.read_json


def export_catalog(path):
    catalog, operations = _export_catalog(path)
    require(legacy.sha256(BASELINE) == BASELINE_HASH, "fixed beta.10 operation denominator changed")
    baseline = read_json(BASELINE)
    require(len(baseline["operationIds"]) == len(set(baseline["operationIds"])) == 389,
            "historical operation denominator is incomplete")
    expected = set(baseline["operationIds"]) | {"helpdesk_email_settings.delete_rule"}
    require(set(operations) == expected and len(operations) == 390,
            "beta.11 must preserve all 389 historical operations and add inbound rule deletion")
    return catalog, operations


legacy.export_catalog = export_catalog


def effect_cardinality(before, first, expected_new, replay=None):
    """Compare independently enumerated, exactly scoped durable effects."""
    require(type(expected_new) is int and 1 <= expected_new <= 1000, "expected effect count must be positive and bounded")
    scope = before.get("scope")
    require(isinstance(scope, dict) and scope.get("campaignId") and scope.get("correlation"),
            "effect snapshots need exact campaign and correlation scope")
    snapshots = [before, first] + ([replay] if replay is not None else [])
    observed, sets = [], []
    for snapshot in snapshots:
        require(snapshot.get("scope") == scope, "effect snapshot scopes differ")
        observed.append(legacy.timestamp(snapshot.get("observedAt")))
        rows = legacy.index(snapshot.get("effects"), "effect")
        require(all(isinstance(row.get("effectKey"), str) and row["effectKey"] for row in rows.values()),
                "every durable effect needs its independently observed key")
        require(len({row["effectKey"] for row in rows.values()}) == len(rows), "duplicate durable effect keys observed")
        sets.append({identity: row["effectKey"] for identity, row in rows.items()})
    require(observed == sorted(observed) and len(set(observed)) == len(observed),
            "effect snapshots must be separately captured in order")
    require(all(sets[1].get(identity) == key for identity, key in sets[0].items()),
            "existing effects disappeared or changed identity")
    require(len(set(sets[1]) - set(sets[0])) == expected_new, "first execution has unexpected durable effect cardinality")
    if replay is not None:
        require(sets[2] == sets[1], "replay added, removed or replaced a durable effect")
    return {"newEffects": expected_new, "replayAddedEffects": 0 if replay is not None else None}


class Validator(legacy.Validator):
    def transport_boundary(self):
        summary = self.evidence(self.data.get("transportBoundary"))
        require(summary.get("format") == "rateldesk-beta11-body-boundary/v1" and summary.get("completed") is True,
                "live HTTP body boundary matrix is incomplete")
        require(summary.get("version") == VERSION and summary.get("sourceRevision") == self.context["sourceRevision"],
                "transport boundary belongs to another release")
        start, end = self.fresh(summary.get("startedAt")), self.fresh(summary.get("completedAt"))
        require(start <= end <= self.functional, "transport boundary must complete before functional completion")
        for name in ("identityBefore", "identityAfter"):
            identity = self.evidence(summary.get(name))
            require(start <= self.fresh(identity.get("observedAt")) <= end and
                    identity.get("body", {}).get("version") == VERSION and
                    identity.get("body", {}).get("commitHash") == self.context["sourceRevision"],
                    "transport deployment identity changed or was not observed")
        limit = summary.get("maximumBodyBytes")
        require(type(limit) is int and 1 < limit <= 30_000_000, "invalid effective decoded-body limit")
        if limit != 30_000_000:
            configured = self.evidence(self.data.get("transportLimitConfiguration"))
            require(configured.get("maximumBodyBytes") == limit and self.fresh(configured.get("observedAt")) <= start,
                    "a smaller body limit needs independent effective configuration evidence")
        expected = {(path, framing, size) for path in ("/mcp", "/mcp/")
                    for framing in ("content-length", "chunked") for size in (limit - 1, limit, limit + 1)}
        if limit == 30_000_000:
            expected.add(("/mcp/", "chunked", 29_990_208))
        rows = summary.get("cases", [])
        keys = [(row.get("path"), row.get("framing"), row.get("decodedBodyBytes")) for row in rows]
        require(len(keys) == len(set(keys)) and set(keys) == expected,
                "every route/framing/below/at/above body-boundary case is required exactly once")
        rpc_ids = set()
        for row in rows:
            request, response = self.evidence(row.get("request")), self.evidence(row.get("response"))
            require((request.get("path"), request.get("framing"), request.get("decodedBodyBytes")) ==
                    (row["path"], row["framing"], row["decodedBodyBytes"]), "transport request identity mismatch")
            require(request.get("method") == "initialize" and request.get("rpcId") and
                    legacy.HASH.fullmatch(request.get("bodySha256", "")), "transport control metadata is incomplete")
            require(request["rpcId"] not in rpc_ids, "transport matrix reused a previous RPC identity")
            rpc_ids.add(request["rpcId"])
            require(start <= self.fresh(request.get("observedAt")) <= self.fresh(response.get("observedAt")) <= end,
                    "transport captures are outside the matrix interval")
            body = response.get("body", {})
            if row["decodedBodyBytes"] <= limit:
                require(response.get("httpStatus") == 200 and body.get("id") == request["rpcId"] and
                        body.get("result", {}).get("serverInfo"), "valid boundary initialize did not succeed")
            else:
                require(response.get("httpStatus") == body.get("status") == 413 and
                        body.get("code") == "request_too_large" and body.get("limit") == "body" and
                        body.get("maximumBodyBytes") == limit, "oversized body needs the deliberate documented 413")

    def validate(self):
        result = super().validate()
        self.transport_boundary()
        result["httpBodyBoundaryValidated"] = True
        result["validatedEvidenceFiles"] = len(self.files)
        return result

    def verify_effects(self, case, replay=False, expected_new=None):
        proof = case.get("effectCardinality", {})
        before, first = self.evidence(proof.get("before")), self.evidence(proof.get("first"))
        repeated = self.evidence(proof.get("replay")) if replay else None
        snapshots = [before, first] + ([repeated] if replay else [])
        for snapshot in snapshots:
            require(snapshot.get("scope", {}).get("campaignId") == self.context["campaignId"],
                    "effect cardinality belongs to another campaign")
            require(legacy.timestamp(case["startedAt"]) <= self.fresh(snapshot.get("observedAt"))
                    <= legacy.timestamp(case["completedAt"]), "effect snapshot is outside its functional case")
        expected = proof.get("expectedNewEffects")
        require(expected_new is None or expected == expected_new, "direct send must create exactly one durable outbox effect")
        return effect_cardinality(before, first, expected, repeated)

    def operation_coverage(self):
        coverage = super().operation_coverage()
        row = next(row for row in self.data["operations"] if row["id"] == "helpdesk_mail.send")
        case = self.case(row["positiveCaseId"], "positive", "helpdesk_mail.send")
        self.verify_effects(case, expected_new=1)
        require("direct_send_delivery_matches_effect" in case["verifiedAssertions"] and
                {"storage", "transport_capture"} <= case["verifiedSurfaces"],
                "direct mail needs its actual outbox effect and independent provider delivery")
        return coverage

    def scenarios_and_history(self):
        super().scenarios_and_history()
        support = next(row for row in self.data["scenarios"] if row["id"] == "support")
        self.verify_effects(self.case(support["cases"]["concurrent_approval_decisions"], "positive"), replay=True)

    def case(self, identity, kind=None, operation=None):
        require(isinstance(identity, str) and identity in self.data["cases"], "missing case receipt")
        if identity in self.case_cache:
            return super().case(identity, kind, operation)
        row = self.evidence(self.data["cases"][identity])
        # Personal-notification isolation can legitimately return updated:0.
        # Keep the actual wire response and require fresh, exact before/after
        # owner readback. This exception never supplies positive mutation or
        # administrator-denial coverage.
        if row.get("operation") != "helpdesk_notifications.mark_read" or row.get("kind") != "expected_negative":
            return super().case(identity, kind, operation)
        response = self.evidence(row.get("response"))
        result = legacy.tool_response(response)
        if result.get("success") is not True:
            return super().case(identity, kind, operation)
        require(row.get("context") == self.context and row.get("id") == identity,
                "isolation case belongs to another campaign/source/catalog")
        require(row.get("outcome") == "PASS" and row.get("origin") == "fresh_deployed"
                and row.get("transport") == "http", "isolation needs fresh deployed HTTP MCP")
        start, end = self.fresh(row.get("startedAt")), self.fresh(row.get("completedAt"))
        require(start <= end <= self.functional, "isolation case exceeds functional window")
        require(row.get("actor") in self.actors and self.actors[row["actor"]].get("kind") == "scoped",
                "zero-effect notification isolation needs the observed ordinary actor")
        request = self.evidence(row.get("request"))
        params = request.get("params", {})
        arguments = params.get("arguments", {})
        require(request.get("method") == "tools/call" and params.get("name") == "helpdesk_notifications"
                and arguments.get("operation") == "mark_read" and arguments.get("confirm") is True,
                "isolation must be an actual confirmed mark-read attempt")
        require(request.get("id") is not None and request.get("id") == response.get("id"),
                "isolation RPC IDs differ")
        updated = result.get("data", {}).get("updated")
        require(type(updated) is int and updated == 0 and row.get("effectObserved") is False,
                "isolation response did not report exactly zero changes")
        isolation = row.get("isolationReadback", {})
        require(isolation.get("ownerActor") in self.actors and isolation["ownerActor"] != row["actor"],
                "foreign notification needs a distinct observed owner actor")
        before_ref, after_ref = isolation.get("before"), isolation.get("after")
        before, after = self.evidence(before_ref), self.evidence(after_ref)
        require(before_ref["sha256"] != after_ref["sha256"], "isolation reused the same capture")
        require(start <= self.fresh(before.get("observedAt")) < self.fresh(after.get("observedAt")) <= end,
                "isolation readbacks must be fresh and ordered")
        old, new = before.get("body"), after.get("body")
        require(isinstance(old, dict) and old.get("id") and old == new,
                "foreign notification state changed or lacked an exact record readback")
        require(arguments.get("request", {}).get("ids") == [old["id"]],
                "isolation readback does not identify the selected notification")
        names, surfaces = self.assertions(row["postcondition"],
                                          (row["request"]["sha256"], row["response"]["sha256"]))
        require("other_actor_read_marker_unchanged" in names and {"api", "storage"} <= surfaces,
                "foreign marker isolation needs its actual API and storage postconditions")
        require(start <= legacy.timestamp(row["postcondition"]["observedAt"]) <= end,
                "isolation postcondition is outside its case")
        row.update(verifiedAssertions=names, verifiedSurfaces=surfaces,
                   wireStatus=result.get("status"), wireHttpStatus=response.get("httpStatus"))
        self.case_cache[identity] = row
        return super().case(identity, kind, operation)


PREREQUISITES = {
    "release_identity": ["version_source_and_digests_match", "live_catalog_matches"],
    "authority": ["explicit_owner_grant_observed", "ordinary_actor_stays_scoped"],
    "baseline": ["owned_fixture_registry_ready", "settings_and_unrelated_record_baseline_captured"],
    "mail": ["ready_global_sender", "owned_recipient_capture", "unrelated_queue_isolated", "outbox_read_access"],
    "netratel": ["exact_target_online", "current_target_profile_revision", "tenant_visibility_and_policy_admitted", "paired_m2m_healthy"],
    "netclaw": ["saved_pairing_authenticates", "actual_provider_reply_observed"],
    "graph_dataset": ["approved_graph_fixture_available"],
    "customer_authentication": ["mode_specific_account_fixture_ready"],
}


def prerequisite_template():
    return {"format": "rateldesk-beta11-prerequisites/v1", "authenticationMode": None,
            "items": [{"id": name, "state": "NOT_CHECKED", "observedAt": None,
                       "assertions": [{"id": key, "evidence": None, "pointer": None, "expected": None}
                                      for key in assertions], "affectedCases": [], "nextAction": None}
                      for name, assertions in PREREQUISITES.items()]}


def check_prerequisites(data, evidence_root):
    require(data.get("format") == "rateldesk-beta11-prerequisites/v1", "unexpected prerequisite format")
    require(data.get("authenticationMode") in {"Local", "Authentik"}, "record the observed authentication mode")
    rows = legacy.index(data.get("items"), "prerequisite")
    require(set(rows) == set(PREREQUISITES), "prerequisite checklist is incomplete")
    # Reuse the same bounded, hashed, no-traversal evidence reader as final validation.
    reader = object.__new__(legacy.Validator)
    reader.root, reader.files, reader.bytes = evidence_root.resolve(), {}, 0
    missing = []
    for name, required in PREREQUISITES.items():
        row = rows[name]
        if row.get("state") != "READY":
            missing.append({"id": name, "state": row.get("state"), "affectedCases": row.get("affectedCases", [])})
            continue
        at = legacy.timestamp(row.get("observedAt"))
        require(at <= datetime.now(timezone.utc), "prerequisite observation is in the future")
        assertions = legacy.index(row.get("assertions"), "prerequisite assertion")
        require(set(assertions) == set(required), "required prerequisite assertion is missing")
        for assertion in assertions.values():
            evidence = reader.evidence(assertion.get("evidence"))
            require(assertion.get("expected") is True and
                    json.dumps(legacy.pointer(evidence, assertion.get("pointer")), sort_keys=True)
                    == json.dumps(assertion["expected"], sort_keys=True),
                    "prerequisite assertion contradicts its capture")
    return {"ready": not missing, "missing": missing,
            "scope": "Prerequisite evidence only; no operation or production acceptance implied"}


def progress(receipt, catalog_path):
    _, expected = export_catalog(catalog_path)
    rows = legacy.index(receipt.get("operations"), "operation")
    require(set(rows) == set(expected), "progress ledger must contain every beta.11 operation exactly once")
    states = {}
    incomplete = []
    for identity, row in rows.items():
        state = row.get("state", "NOT_RUN")
        states[state] = states.get(state, 0) + 1
        allowed_exclusion = identity in legacy.HTTP_EXCLUSIONS or (
            receipt.get("campaign", {}).get("authenticationMode") == "Local" and identity in legacy.LOCAL_EQUIVALENTS)
        required = ("guardCaseId", "equivalentCaseId") if state == "EXCLUDED" and identity in legacy.LOCAL_EQUIVALENTS else (
            ("guardCaseId",) if state == "EXCLUDED" else ("positiveCaseId",))
        if state == "PASS" and expected[identity]["confirmationRequired"]:
            required += ("previewCaseId",)
        if state == "PASS" and expected[identity].get("boundedIntegrationCredential", {}).get("support") == "denied_by_policy":
            required += ("denialCaseId",)
        if state not in {"PASS", "EXCLUDED"} or (state == "EXCLUDED" and not allowed_exclusion) or any(
                not row.get(key) or row[key] not in receipt.get("cases", {}) for key in required):
            incomplete.append(identity)
    spec = legacy.contract()
    def missing_mappings(name, requirements):
        actual = legacy.index(receipt.get(name), name)
        require(set(actual) == {row["id"] for row in requirements}, "required " + name + " rows are missing")
        return [row["id"] + "/" + case for row in requirements for case in row["cases"]
                if not actual[row["id"]].get("cases", {}).get(case)
                or actual[row["id"]]["cases"][case] not in receipt.get("cases", {})]
    return {"readiness": "NOT_VALIDATED", "operationCount": len(expected), "recordedStates": states,
            "operationsNeedingEvidence": incomplete,
            "domainMappingsNeeded": missing_mappings("scenarios", spec["scenarios"]),
            "historicalMappingsNeeded": missing_mappings("historicalObligations", spec["historicalObligations"]),
            "functionalCompletedAt": receipt.get("campaign", {}).get("functionalCompletedAt"),
            "transportBoundaryRecorded": receipt.get("transportBoundary") is not None,
            "soakRecorded": receipt.get("soak") is not None, "cleanupRecorded": receipt.get("cleanup") is not None,
            "scope": "Planning inventory; recorded PASS labels have not been validated"}


def initialize(directory, catalog_path):
    require(not directory.exists(), "refusing to overwrite or merge an existing campaign workspace")
    receipt = legacy.template(catalog_path)
    receipt["transportBoundary"] = None
    receipt["transportLimitConfiguration"] = None
    directory.mkdir(mode=0o700, parents=False)
    files = {"receipt.json": receipt, "prerequisites.json": prerequisite_template(),
             "fixture-registry.json": {"format": "rateldesk-beta11-fixtures/v1", "campaignId": None,
                 "baseline": None, "fixtures": [], "settings": [], "uncertainEffects": []}}
    for name, data in files.items():
        (directory / name).write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    return {"readiness": "NOT_RUN", "operations": len(receipt["operations"]),
            "domainCases": sum(len(row["cases"]) for row in receipt["scenarios"]),
            "historicalObligations": len(receipt["historicalObligations"]), "created": list(files)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("init", "status", "check-prerequisites", "validate"))
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--receipt", type=Path)
    parser.add_argument("--prerequisites", type=Path)
    parser.add_argument("--evidence-root", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    try:
        export_catalog(args.catalog)
        if args.mode == "init":
            require(args.output is not None, "init requires a new private --output directory")
            require(not args.output.resolve().is_relative_to(Path(__file__).resolve().parents[2]),
                    "campaign workspace must be outside the checkout")
            result = initialize(args.output, args.catalog)
        elif args.mode == "status":
            require(args.receipt is not None, "status requires --receipt")
            result = progress(read_json(args.receipt), args.catalog)
        elif args.mode == "check-prerequisites":
            require(args.prerequisites is not None and args.evidence_root is not None,
                    "check-prerequisites requires --prerequisites and --evidence-root")
            result = check_prerequisites(read_json(args.prerequisites), args.evidence_root)
        else:
            require(args.manifest is not None and args.receipt is not None and args.evidence_root is not None,
                    "validate requires --manifest, --receipt and --evidence-root")
            result = Validator(read_json(args.receipt), args.evidence_root, args.catalog, args.manifest).validate()
        print(json.dumps(result, indent=2))
        return 1 if result.get("ready") is False else 0
    except (Invalid, OSError, ValueError, KeyError, TypeError, IndexError, RecursionError, legacy.ET.ParseError) as error:
        # No raw capture bodies or private selector values in diagnostics.
        message = str(error) if isinstance(error, Invalid) else type(error).__name__
        message = {"candidate version must be exactly beta.8": "candidate version must be exactly beta.11",
                   "catalog is not the beta.8 owner-operation revision": "catalog is not the beta.11 owner-operation revision"}.get(message, message)
        print("NOT_READY: " + message, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

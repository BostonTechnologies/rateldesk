#!/usr/bin/env python3
"""Beta.12 private campaign workspace, evidence planning and final acceptance gate.

The beta.8 and beta.11 validators remain immutable. This adapter retains their
operation/domain/history/effect/soak requirements and adds granular fixture
prerequisites. Planning commands never modify receipts or manufacture a PASS.
Offline checks establish consistency, not the authenticity of supplied captures.
"""

import argparse
import copy
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import sys


VERSION = "0.1.2-beta.12"
FORMAT = "rateldesk-beta12-campaign/v1"
PREREQUISITE_FORMAT = "rateldesk-beta12-prerequisites/v1"
CATALOG_REVISION = "2026-10-25.beta11-contracts-v25"
RESIDUAL_FIXTURES = Path(__file__).with_name("beta12-residual-fixtures.json")
RESIDUAL_FIXTURES_HASH = "27cde4655950ea2055e8c0911b24b72778fa28cc36c809405a2f4c92fc0350fd"

_spec = importlib.util.spec_from_file_location(
    "rateldesk_beta12_inherited_gate", Path(__file__).with_name("validate-beta11-campaign.py"))
inherited = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(inherited)
legacy = inherited.legacy
# The isolated module instances do not alter another importer or historical file.
inherited.VERSION, inherited.FORMAT = VERSION, FORMAT
legacy.VERSION, legacy.FORMAT = VERSION, FORMAT
Invalid, require, read_json = inherited.Invalid, inherited.require, inherited.read_json
export_catalog = inherited.export_catalog


def fixture_lanes():
    require(legacy.sha256(RESIDUAL_FIXTURES) == RESIDUAL_FIXTURES_HASH,
            "fixed beta.11 residual fixture mapping changed")
    document = read_json(RESIDUAL_FIXTURES)
    lanes = legacy.index(document.get("lanes"), "fixture lane")
    operations = [operation for lane in lanes.values() for operation in lane["operationIds"]]
    require(len(lanes) == 12 and len(operations) == len(set(operations)) == 59,
            "all 59 prior residual operations need exactly one fixture lane")
    return lanes


LANES = fixture_lanes()
PREREQUISITES = {**inherited.PREREQUISITES,
    **{name: lane["assertions"] for name, lane in LANES.items()},
    "callbacks": ["owned_correlation_and_phase_controls_available", "authenticated_503_and_403_controls_available",
                  "bounded_stream_snapshot_available"],
    "transport_configuration": ["effective_web_and_mcp_limits_observed", "public_ingress_route_identified"],
}


def prerequisite_template():
    return {"format": PREREQUISITE_FORMAT, "context": None, "campaignStartedAt": None,
            "authenticationMode": None,
            "items": [{"id": name, "state": "NOT_CHECKED", "observedAt": None,
                       "assertions": [{"id": key, "evidence": None, "pointer": None, "expected": None}
                                      for key in assertions],
                       "affectedOperations": list(LANES.get(name, {}).get("operationIds", [])),
                       "affectedCases": [], "nextAction": None}
                      for name, assertions in PREREQUISITES.items()]}


def check_prerequisites(data, validator):
    require(data.get("format") == PREREQUISITE_FORMAT, "unexpected prerequisite format")
    require(data.get("context") == validator.context, "prerequisites belong to another campaign/source/catalog")
    require(data.get("authenticationMode") == validator.mode, "prerequisite authentication mode differs")
    require(legacy.timestamp(data.get("campaignStartedAt")) == validator.started,
            "prerequisite campaign start differs")
    rows = legacy.index(data.get("items"), "prerequisite")
    require(set(rows) == set(PREREQUISITES), "granular prerequisite checklist is incomplete")
    missing = []
    for name, required in PREREQUISITES.items():
        row = rows[name]
        require(row.get("state") in {"NOT_CHECKED", "BLOCKED_PREREQUISITE", "READY"},
                "unsupported prerequisite state")
        require(row.get("affectedOperations") == LANES.get(name, {}).get("operationIds", []),
                "fixed prerequisite operation mapping differs")
        if row["state"] != "READY":
            if row["state"] == "BLOCKED_PREREQUISITE":
                require(isinstance(row.get("nextAction"), str) and row["nextAction"].strip(),
                        "blocked prerequisite needs a concrete next action")
            missing.append({"id": name, "state": row["state"],
                            "affectedOperations": row["affectedOperations"],
                            "affectedCases": row.get("affectedCases", []), "nextAction": row.get("nextAction")})
            continue
        observed = validator.fresh(row.get("observedAt"))
        require(observed <= validator.functional, "prerequisite was only observed after functional completion")
        assertions = legacy.index(row.get("assertions"), "prerequisite assertion")
        require(set(assertions) == set(required), "required prerequisite assertion is missing")
        for assertion in assertions.values():
            capture = validator.evidence(assertion.get("evidence"))
            require(capture.get("context") == validator.context,
                    "prerequisite capture belongs to another campaign/source/catalog")
            require(validator.fresh(capture.get("observedAt")) <= observed,
                    "prerequisite capture was observed after the readiness claim")
            require(assertion.get("expected") is True and
                    legacy.pointer(capture, assertion.get("pointer")) is True,
                    "prerequisite assertion contradicts its capture")
    return {"readiness": "NOT_VALIDATED", "ready": not missing, "missing": missing,
            "residualOperationCount": 59,
            "scope": "Fresh prerequisite evidence only; no operation or production acceptance implied"}


class Validator(inherited.Validator):
    def transport_configuration(self, limit, start):
        configuration = self.evidence(self.data.get("transportLimitConfiguration"))
        require(configuration.get("context") == self.context,
                "effective transport configuration belongs to another campaign/source/catalog")
        observed = self.fresh(configuration.get("observedAt"))
        require(observed <= start and configuration.get("maximumBodyBytes") == limit,
                "effective transport configuration must precede and match the boundary probes")
        components = configuration.get("components", {})
        require(set(components) == {"web", "mcp-http"}, "observe effective Web and MCP host body limits")
        effective_limits = []
        for row in components.values():
            capture = self.evidence(row.get("evidence"))
            require(capture.get("kind") == "effective_deployment_configuration" and
                    capture.get("context") == self.context and self.fresh(capture.get("observedAt")) <= observed,
                    "transport limits need independent current deployment configuration captures")
            actual = legacy.pointer(capture, row.get("pointer"))
            require(type(actual) is int and 1 < actual <= 30_000_000,
                    "invalid observed component decoded-body limit")
            effective_limits.append(actual)
        require(min(effective_limits) == limit, "boundary limit differs from the effective component minimum")

    def transport_boundary(self):
        summary = self.evidence(self.data.get("transportBoundary"))
        require(summary.get("format") == "rateldesk-beta12-body-boundary/v1" and summary.get("completed") is True,
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
        self.transport_configuration(limit, start)
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
            require(request.get("uploadBehavior") == "send-complete-body-before-reading-response",
                    "boundary probes must retain the eager-upload client behavior")
            require(request["rpcId"] not in rpc_ids, "transport matrix reused a previous RPC identity")
            rpc_ids.add(request["rpcId"])
            require(start <= self.fresh(request.get("observedAt")) <= self.fresh(response.get("observedAt")) <= end,
                    "transport captures are outside the matrix interval")
            body = response.get("body", {})
            content_type = response.get("contentType")
            if row["decodedBodyBytes"] <= limit:
                require(response.get("httpStatus") == 200 and body.get("id") == request["rpcId"] and
                        body.get("result", {}).get("serverInfo"), "valid boundary initialize did not succeed")
            else:
                require(response.get("httpStatus") == body.get("status") == 413 and
                        body.get("code") == "request_too_large" and body.get("limit") == "body" and
                        body.get("maximumBodyBytes") == limit and
                        isinstance(content_type, str) and
                        content_type.split(";", 1)[0].strip().lower() == "application/problem+json",
                        "oversized body needs the deliberate documented problem+json 413")

    def validate(self):
        result = super().validate()
        prerequisites = self.evidence(self.data.get("prerequisites"))
        require(check_prerequisites(prerequisites, self)["ready"], "granular prerequisites remain incomplete")
        result.update(granularPrerequisitesValidated=True, validatedEvidenceFiles=len(self.files))
        return result


def planning_validator(receipt, evidence_root, catalog_path, manifest_path, now=None):
    """Validate identity and case provenance without declaring functional completion.

    The inherited provenance routine also checks final-receipt status fields. Only
    those status fields and the open time horizon are substituted in a private copy
    to reuse its full identity checks. The original receipt is restored for all case
    reads; no file or readiness label is written and validate() is never called.
    """
    require(receipt.get("format") == FORMAT, "planning requires a beta.12 receipt")
    now = now or datetime.now(timezone.utc)
    view = copy.deepcopy(receipt)
    campaign = view["campaign"]
    horizon = campaign.get("completedAt") or now.isoformat()
    view.update(readiness="PASS", productBlockers=[], unresolvedFailures=0)
    campaign["completedAt"] = horizon
    campaign["functionalCompletedAt"] = campaign.get("functionalCompletedAt") or horizon
    validator = Validator(view, evidence_root, catalog_path, manifest_path, now=now)
    validator.provenance()
    validator.identities()
    require(isinstance(receipt.get("cases"), dict) and len(receipt["cases"]) <= 10000,
            "case registry must be bounded")
    validator.data = receipt
    return validator


def mapping_plan(validator):
    valid, invalid = {}, []
    for identity in validator.data["cases"]:
        try:
            valid[identity] = validator.case(identity)
        except (Invalid, OSError, ValueError, KeyError, TypeError, IndexError, RecursionError, legacy.ET.ParseError) as error:
            invalid.append({"caseId": identity, "reason": str(error) if isinstance(error, Invalid) else type(error).__name__})
    rows = []
    for section, requirements in (("scenarios", validator.spec["scenarios"]),
                                  ("historicalObligations", validator.spec["historicalObligations"])):
        existing = legacy.index(validator.data.get(section), section)
        require(set(existing) == {row["id"] for row in requirements}, "required mapping rows are missing")
        for requirement in requirements:
            for name, specification in requirement["cases"].items():
                if section == "scenarios":
                    kind, assertion = specification["kind"], specification["assertion"]
                    surfaces, allowed = set(specification["surfaces"]), None
                else:
                    kind, operation = specification, requirement["operation"]
                    if validator.mode == "Local" and (requirement["id"], name) in legacy.LOCAL_SOURCE_CASES:
                        kind, operation = "implementation", None
                    allowed = None if operation is None else (
                        legacy.LOCAL_EQUIVALENTS.get(operation, {operation}) if validator.mode == "Local" else {operation})
                    assertion, surfaces = f"historical:{requirement['id']}/{name}", set()
                candidates = []
                for identity, case in valid.items():
                    matches = (case["kind"] == kind and assertion in case["verifiedAssertions"] and
                               surfaces <= case["verifiedSurfaces"] and
                               (allowed is None or case.get("operation") in allowed))
                    if matches and section == "scenarios" and requirement["id"] == "support" and name == "concurrent_approval_decisions":
                        try:
                            validator.verify_effects(case, replay=True)
                        except Invalid:
                            matches = False
                    if matches:
                        candidates.append(identity)
                current = existing[requirement["id"]].get("cases", {}).get(name)
                rows.append({"section": section, "id": requirement["id"], "slot": name,
                             "required": {"kind": kind, "assertion": assertion, "surfaces": sorted(surfaces),
                                          "operationIds": sorted(allowed) if allowed is not None else None},
                             "mappedCaseId": current, "mappingMatches": current is not None and current in candidates,
                             "candidateCaseIds": sorted(candidates)})
    return {"readiness": "NOT_VALIDATED", "receiptModified": False,
            "validatedCaseCount": len(valid), "invalidCases": invalid, "mappings": rows,
            "scope": "Per-case evidence candidates only; review semantics and run the complete final gate. No mappings or PASS labels were written."}


def initialize(directory, catalog_path):
    require(not directory.exists(), "refusing to overwrite or merge an existing campaign workspace")
    _, operations = export_catalog(catalog_path)
    require({operation for lane in LANES.values() for operation in lane["operationIds"]} <= set(operations),
            "residual fixture map contains an unreleased operation")
    receipt = legacy.template(catalog_path)
    receipt.update(transportBoundary=None, transportLimitConfiguration=None, prerequisites=None)
    directory.mkdir(mode=0o700, parents=False)
    files = {"receipt.json": receipt, "prerequisites.json": prerequisite_template(),
             "fixture-registry.json": {"format": "rateldesk-beta12-fixtures/v1", "campaignId": None,
                 "baseline": None, "baselineComparisons": [], "fixtures": [], "settings": [], "uncertainEffects": []}}
    for name, data in files.items():
        (directory / name).write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    return {"readiness": "NOT_RUN", "operations": len(receipt["operations"]),
            "domainCases": sum(len(row["cases"]) for row in receipt["scenarios"]),
            "historicalObligations": len(receipt["historicalObligations"]),
            "cleanupAssertions": len(legacy.contract()["cleanupAssertions"]),
            "residualFixtureLanes": len(LANES), "created": list(files)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("init", "status", "check-prerequisites", "mapping-plan", "validate"))
    parser.add_argument("--catalog", type=Path, required=True)
    for flag in ("manifest", "receipt", "prerequisites", "evidence-root", "output"):
        parser.add_argument("--" + flag, type=Path)
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
            receipt = read_json(args.receipt)
            require(receipt.get("format") == FORMAT, "status requires a beta.12 receipt")
            result = inherited.progress(receipt, args.catalog)
        else:
            require(args.manifest is not None and args.receipt is not None and args.evidence_root is not None,
                    "this mode requires --manifest, --receipt and --evidence-root")
            receipt = read_json(args.receipt)
            if args.mode == "validate":
                result = Validator(receipt, args.evidence_root, args.catalog, args.manifest).validate()
            else:
                validator = planning_validator(receipt, args.evidence_root, args.catalog, args.manifest)
                if args.mode == "mapping-plan":
                    result = mapping_plan(validator)
                else:
                    require(args.prerequisites is not None, "check-prerequisites requires --prerequisites")
                    result = check_prerequisites(read_json(args.prerequisites), validator)
        print(json.dumps(result, indent=2))
        return 1 if result.get("ready") is False else 0
    except (Invalid, OSError, ValueError, KeyError, TypeError, IndexError, RecursionError, legacy.ET.ParseError) as error:
        message = str(error) if isinstance(error, Invalid) else type(error).__name__
        message = message.replace("exactly beta.8", "exactly beta.12").replace("beta.8 owner-operation", "released owner-operation")
        print("NOT_READY: " + message, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

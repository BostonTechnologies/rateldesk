#!/usr/bin/env python3
"""Synthetic, offline failure tests for the beta.8 receipt gate; no live acceptance."""

import copy
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("beta8_gate", Path(__file__).with_name("validate-beta8-campaign.py"))
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)


class CampaignGateTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="rateldesk-beta8-gate-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.sequence = 0
        self.source = hashlib.sha1(b"offline synthetic candidate").hexdigest()
        self.now = datetime(2026, 10, 19, 2, tzinfo=timezone.utc)
        self.prepare_catalog()
        self.prepare_receipt()

    def write(self, value, name=None):
        self.sequence += 1
        path = self.root / (name or f"evidence-{self.sequence:05d}.json")
        path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
        return {"path": path.name, "sha256": gate.sha256(path)}

    def read(self, reference):
        return gate.read_json(self.root / reference["path"])

    def replace(self, reference, transform):
        value = self.read(reference)
        transform(value)
        return self.write(value)

    @staticmethod
    def response(data=None, success=True, status="completed"):
        return {"jsonrpc": "2.0", "id": 1, "result": {"structuredContent": {"success": success, "status": status, "data": data}}}

    def prepare_catalog(self):
        # Deliberately fabricated catalog fixture, using the immutable public
        # operation denominator. It is never written to a release artifact.
        original = gate.read_json(Path(__file__).resolve().parents[2] / "docs/contracts/mcp-operation-catalog-beta7.json")
        self.ops = {f"{row['tool']}.{row['operation']}": copy.deepcopy(row) for row in original["operations"]}
        for identity in gate.REQUIRED_BETA8_OPERATIONS:
            tool, operation = identity.split(".", 1)
            self.ops.setdefault(identity, {"tool": tool, "operation": operation, "readOnly": False,
                "confirmationRequired": True, "boundedIntegrationCredential": {"support": "denied_by_policy"},
                "method": "POST", "pathTemplate": "/synthetic-only", "effect": "write"})
        groups = {}
        for identity, row in self.ops.items():
            groups.setdefault(row["tool"], []).append(row["operation"])
        schemas = [{"tool": tool, "inputSchema": {"type": "object", "properties": {"operation": {"type": "string", "enum": sorted(operations)}}}} for tool, operations in sorted(groups.items())]
        self.catalog = {"catalogRevision": gate.CATALOG_REVISION, "operations": list(self.ops.values()),
                        "operationCount": len(self.ops), "toolSchemas": schemas}
        self.catalog["catalogHash"] = gate.native_hash(self.catalog["operations"])
        self.catalog["schemaHash"] = gate.native_hash(schemas)
        self.catalog_ref = self.write(self.catalog, "synthetic-catalog.json")
        self.catalog_path = self.root / self.catalog_ref["path"]

    def observation(self, assertion_ids, surfaces=("api",)):
        facts = dict.fromkeys(assertion_ids, True)
        reference = self.write({"unitTestOnly": True, "facts": facts})
        assertions = []
        for number, surface in enumerate(surfaces):
            for identity in assertion_ids:
                assertions.append({"id": identity if number == 0 else identity + ":" + surface,
                    "surface": surface, "evidence": reference,
                    "pointer": "/facts/" + identity.replace("~", "~0").replace("/", "~1"), "expected": True})
        return {"independent": True, "observedAt": "2026-10-19T00:19:00Z", "assertions": assertions}

    def make_case(self, kind="positive", operation="helpdesk_health.get", assertions=("postcondition",), surfaces=("api",)):
        identity = f"case-{self.sequence:05d}"
        row = {"id": identity, "context": self.context, "outcome": "PASS", "kind": kind,
               "startedAt": "2026-10-19T00:11:00Z", "completedAt": "2026-10-19T00:20:00Z",
               "origin": "candidate_ci" if kind == "implementation" else "fresh_deployed"}
        if kind == "implementation":
            path = self.root / f"{identity}.trx"
            path.write_text('<TestRun xmlns="urn:fixture"><Results><UnitTestResult testName="Synthetic failure-gate fixture" outcome="Passed"/></Results></TestRun>')
            row.update(testResults={"path": path.name, "sha256": gate.sha256(path)}, assertionIds=list(assertions))
        else:
            tool, name = operation.split(".", 1)
            request = {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": tool,
                "arguments": {"operation": name, "confirm": kind == "positive", "request": {"syntheticOnly": True}}}}
            result = self.response({"recordObserved": True}, kind == "positive", "confirmation_required" if kind == "preview" else "forbidden" if kind == "expected_negative" else "completed")
            row.update(transport="http", operation=operation, actor="ordinary" if kind == "expected_negative" else "owner",
                       request=self.write(request), response=self.write(result), effectObserved=kind == "positive",
                       postcondition=self.observation(assertions, surfaces))
        self.receipt["cases"][identity] = self.write(row)
        return identity

    def prepare_receipt(self):
        manifests = {"version": gate.VERSION, "tag": "v" + gate.VERSION, "sourceRevision": self.source,
                     "containers": [], "assets": {}}
        components, natives = {}, {}
        for name, image_family in gate.IMAGE_FAMILIES.items():
            digest = "sha256:" + hashlib.sha256(name.encode()).hexdigest()
            manifests["containers"].append({"image": f"{image_family}:{gate.VERSION}", "digest": digest})
            capture = {"version": gate.VERSION, "sourceRevision": self.source, "digest": digest}
            components[name] = dict(capture, observation=self.write(capture))
        for tool in ("cli", "mcp-stdio"):
            name = f"rateldesk-{tool}-{gate.VERSION}-linux-x64.tar.gz"
            digest = hashlib.sha256(name.encode()).hexdigest()
            manifests["assets"][name] = digest
            natives[tool] = {"asset": name, "version": gate.VERSION, "sourceRevision": self.source, "sha256": digest,
                            "observation": self.write({"version": gate.VERSION, "sourceRevision": self.source, "archiveSha256": digest})}
        manifest_ref = self.write(manifests, "synthetic-release-manifest.json")
        self.manifest_path = self.root / manifest_ref["path"]
        deployment = self.write({"observedAt": "2026-10-19T00:10:01Z", "authenticationMode": "Local",
            "authenticationModeEvidence": self.write({"authenticationMode": "Local"}), "components": components, "nativeTools": natives})
        self.context = {"campaignId": "offline-validator-fixture", "version": gate.VERSION, "tag": "v" + gate.VERSION,
                        "sourceRevision": self.source, "deploymentSha256": deployment["sha256"],
                        "catalogFileSha256": self.catalog_ref["sha256"], "catalogHash": self.catalog["catalogHash"], "schemaHash": self.catalog["schemaHash"]}
        self.receipt = {"format": gate.FORMAT, "readiness": "PASS",
            "campaign": {"id": self.context["campaignId"], "version": gate.VERSION, "tag": "v" + gate.VERSION,
                "sourceRevision": self.source, "authenticationMode": "Local", "startedAt": "2026-10-19T00:10:00Z",
                "functionalCompletedAt": "2026-10-19T00:30:00Z", "completedAt": "2026-10-19T00:50:00Z"},
            "releaseManifest": manifest_ref, "deployment": deployment,
            "catalog": {"export": self.catalog_ref, "catalogHash": self.catalog["catalogHash"], "schemaHash": self.catalog["schemaHash"],
                "capabilities": self.write(self.response({"catalogHash": self.catalog["catalogHash"]})),
                "wireDiscovery": self.write({"result": {"tools": [{"name": row["tool"], "inputSchema": row["inputSchema"]} for row in self.catalog["toolSchemas"]]}})},
            "actors": [{"id": "owner", "kind": "owner", "observedAt": "2026-10-19T00:10:02Z",
                "accessEvidence": self.write(self.response({"isAuthenticated": True, "isHelpdeskAdmin": True, "grantKind": "owner-administration"}))},
                {"id": "ordinary", "kind": "scoped", "observedAt": "2026-10-19T00:10:02Z",
                "accessEvidence": self.write(self.response({"isAuthenticated": True, "isHelpdeskAdmin": False, "grantKind": "ordinary"}))}],
            "cases": {}, "operations": [], "scenarios": [], "historicalObligations": [], "productBlockers": [], "unresolvedFailures": 0}
        for identity, spec in self.ops.items():
            if identity in gate.HTTP_EXCLUSIONS or identity in gate.LOCAL_EQUIVALENTS:
                row = {"id": identity, "state": "EXCLUDED", "guardCaseId": self.make_case("expected_negative", identity, ["explicit_capability_guard"])}
                if identity in gate.LOCAL_EQUIVALENTS:
                    row["equivalentCaseId"] = self.make_case(operation=sorted(gate.LOCAL_EQUIVALENTS[identity])[0])
            else:
                row = {"id": identity, "state": "PASS", "positiveCaseId": self.make_case(operation=identity)}
                if spec["confirmationRequired"]:
                    row["previewCaseId"] = self.make_case("preview", identity)
                if spec.get("boundedIntegrationCredential", {}).get("support") == "denied_by_policy":
                    row["denialCaseId"] = self.make_case("expected_negative", identity)
            self.receipt["operations"].append(row)
        for scenario in gate.contract()["scenarios"]:
            self.receipt["scenarios"].append({"id": scenario["id"], "cases": {name: self.make_case(spec["kind"], assertions=[spec["assertion"]], surfaces=spec["surfaces"])
                for name, spec in scenario["cases"].items()}})
        for obligation in gate.contract()["historicalObligations"]:
            cases = {}
            for name, kind in obligation["cases"].items():
                operation = obligation["operation"] or "helpdesk_health.get"
                if operation in gate.LOCAL_EQUIVALENTS:
                    operation = sorted(gate.LOCAL_EQUIVALENTS[operation])[0]
                if (obligation["id"], name) in gate.LOCAL_SOURCE_CASES:
                    kind = "implementation"
                cases[name] = self.make_case(kind, operation, [f"historical:{obligation['id']}/{name}"])
            self.receipt["historicalObligations"].append({"id": obligation["id"], "cases": cases})
        samples = [{"at": f"2026-10-19T00:{30 + second // 60:02d}:{second % 60:02d}Z", "operation": "helpdesk_health.get",
            "latencyMs": 12, "unexpectedFailure": False,
            "response": self.write(dict(self.response({"healthy": True}), id=second))} for second in range(0, 901, 30)]
        self.receipt["soak"] = self.write({"context": self.context, "startedAt": "2026-10-19T00:30:00Z", "completedAt": "2026-10-19T00:45:00Z",
            "samples": self.write(samples), "totalRequests": len(samples), "unexpectedFailures": 0,
            "budget": self.write({"declaredAt": "2026-10-19T00:00:00Z", "metric": "p95", "limitMs": 1500})})
        cleanup = self.observation(gate.contract()["cleanupAssertions"])
        cleanup.update(context=self.context, observedAt="2026-10-19T00:46:00Z")
        self.receipt["cleanup"] = self.write(cleanup)

    def validator(self):
        return gate.Validator(self.receipt, self.root, self.catalog_path, self.manifest_path, self.now)

    def operation(self, identity):
        return next(row for row in self.receipt["operations"] if row["id"] == identity)

    def change_case(self, identity, transform):
        self.receipt["cases"][identity] = self.replace(self.receipt["cases"][identity], transform)

    def test_complete_synthetic_fixture_checks_all_denominators_without_claiming_live_acceptance(self):
        result = self.validator().validate()
        self.assertEqual(len(self.ops) - 7, result["operationsPassed"])
        self.assertEqual(7, len(result["operationsExcluded"]))
        self.assertEqual(86, result["historicalObligations"])
        self.assertFalse(result["allAuthenticationModesValidated"])

    def test_template_remains_not_run_and_cannot_pass(self):
        self.receipt = gate.template(self.catalog_path)
        with self.assertRaisesRegex(gate.Invalid, "not a completed PASS"):
            self.validator().validate()

    def test_missing_current_operation_or_arbitrary_exclusion_is_rejected(self):
        row = self.operation("helpdesk_datasets.upsert_rows")
        row["state"] = "EXCLUDED"
        with self.assertRaisesRegex(gate.Invalid, "arbitrarily excluded"):
            self.validator().validate()
        self.receipt["operations"].remove(row)
        with self.assertRaisesRegex(gate.Invalid, "every current operation"):
            self.validator().validate()

    def test_local_external_guard_needs_fixed_positive_equivalent(self):
        row = self.operation("helpdesk_admin_mutations.customer_invite")
        row["equivalentCaseId"] = self.operation("helpdesk_health.get")["positiveCaseId"]
        with self.assertRaisesRegex(gate.Invalid, "fixed positive equivalent"):
            self.validator().validate()

    def test_version_source_and_live_digest_must_match_release(self):
        for field, value in (("version", "0.1.2-beta.7"), ("sourceRevision", "a" * 40)):
            with self.subTest(field=field):
                before = self.receipt["campaign"][field]
                self.receipt["campaign"][field] = value
                with self.assertRaises(gate.Invalid): self.validator().validate()
                self.receipt["campaign"][field] = before
        self.receipt["deployment"] = self.replace(self.receipt["deployment"], lambda row: row["components"]["mcp-http"].update(digest="sha256:" + "b" * 64))
        with self.assertRaisesRegex(gate.Invalid, "component identity"):
            self.validator().validate()

    def test_live_tools_schema_drift_is_rejected(self):
        item = self.receipt["catalog"]
        item["wireDiscovery"] = self.replace(item["wireDiscovery"], lambda row: row["result"]["tools"].pop())
        with self.assertRaisesRegex(gate.Invalid, "live discovery"):
            self.validator().validate()

    def test_ordinary_account_cannot_be_relabelled_owner(self):
        actor = self.receipt["actors"][0]
        actor["accessEvidence"] = self.write(self.response({"isAuthenticated": True, "isHelpdeskAdmin": True, "grantKind": "ordinary"}))
        with self.assertRaisesRegex(gate.Invalid, "owner grant"):
            self.validator().validate()

    def test_denial_or_preview_is_not_a_positive(self):
        row = self.operation("helpdesk_datasets.upsert_rows")
        row["positiveCaseId"] = row["previewCaseId"]
        with self.assertRaisesRegex(gate.Invalid, "case kind"):
            self.validator().validate()

    def test_confirmed_write_requires_nonzero_effect_and_independent_observation(self):
        identity = self.operation("helpdesk_datasets.upsert_rows")["positiveCaseId"]
        self.change_case(identity, lambda row: row.update(response=self.write(self.response({"updated": 0}))))
        with self.assertRaisesRegex(gate.Invalid, "zero-effect"):
            self.validator().validate()
        self.change_case(identity, lambda row: row.update(response=self.write(self.response()), effectObserved=False))
        with self.assertRaisesRegex(gate.Invalid, "observed effect"):
            self.validator().validate()

    def test_postcondition_checks_actual_json_value_in_hashed_capture(self):
        identity = self.operation("helpdesk_health.get")["positiveCaseId"]
        self.change_case(identity, lambda row: row["postcondition"]["assertions"][0].update(expected=False))
        with self.assertRaisesRegex(gate.Invalid, "does not equal"):
            self.validator().validate()

    def test_wrong_case_context_and_nonlive_origin_are_rejected(self):
        identity = self.operation("helpdesk_health.get")["positiveCaseId"]
        self.change_case(identity, lambda row: row.update(origin="candidate_ci"))
        with self.assertRaisesRegex(gate.Invalid, "fresh deployed"):
            self.validator().validate()
        self.change_case(identity, lambda row: row.update(context=dict(self.context, sourceRevision="b" * 40)))
        with self.assertRaisesRegex(gate.Invalid, "another campaign"):
            self.validator().validate()

    def test_all_historical_obligations_and_cases_are_mandatory(self):
        original = copy.deepcopy(self.receipt["historicalObligations"])
        self.receipt["historicalObligations"].pop()
        with self.assertRaisesRegex(gate.Invalid, "all 86"):
            self.validator().validate()
        self.receipt["historicalObligations"] = original
        self.receipt["historicalObligations"][0]["cases"].pop("isolation")
        with self.assertRaisesRegex(gate.Invalid, "substantive case omitted"):
            self.validator().validate()

    def test_real_mail_and_provider_evidence_surfaces_cannot_be_replaced_by_api_claims(self):
        row = next(row for row in self.receipt["scenarios"] if row["id"] == "mail")
        identity = row["cases"]["partial_acceptance"]
        self.change_case(identity, lambda case: case["postcondition"].update(assertions=[dict(item, surface="api") for item in case["postcondition"]["assertions"]]))
        with self.assertRaisesRegex(gate.Invalid, "evidence surface"):
            self.validator().validate()

    def test_short_soak_and_observation_gaps_are_rejected(self):
        original = self.receipt["soak"]
        self.receipt["soak"] = self.replace(original, lambda row: row.update(completedAt="2026-10-19T00:31:00Z"))
        with self.assertRaisesRegex(gate.Invalid, "900–3600"):
            self.validator().validate()
        self.receipt["soak"] = original
        soak = self.read(original)
        samples = self.read(soak["samples"])
        samples[10]["at"] = "2026-10-19T00:44:00Z"
        soak["samples"] = self.write(samples)
        self.receipt["soak"] = self.write(soak)
        with self.assertRaisesRegex(gate.Invalid, "unobserved gap"):
            self.validator().validate()

    def test_replayed_soak_response_cannot_substitute_for_fresh_requests(self):
        soak = self.read(self.receipt["soak"])
        samples = self.read(soak["samples"])
        samples[-1]["response"] = samples[0]["response"]
        soak["samples"] = self.write(samples)
        self.receipt["soak"] = self.write(soak)
        with self.assertRaisesRegex(gate.Invalid, "reuses a previous response"):
            self.validator().validate()

    def test_retroactive_budget_and_missing_cleanup_are_rejected(self):
        original = self.receipt["soak"]
        soak = self.read(original)
        soak["budget"] = self.write({"declaredAt": "2026-10-19T00:46:00Z", "metric": "p95", "limitMs": 1500})
        self.receipt["soak"] = self.write(soak)
        with self.assertRaisesRegex(gate.Invalid, "declared before"):
            self.validator().validate()
        self.receipt["soak"] = original
        self.receipt["cleanup"] = self.replace(self.receipt["cleanup"], lambda row: row["assertions"].pop())
        with self.assertRaisesRegex(gate.Invalid, "cleanup or uncertainty"):
            self.validator().validate()

    def test_hash_path_and_symlink_tampering_are_rejected(self):
        validator = self.validator()
        for reference in ({"path": "../outside.json", "sha256": "a" * 64}, {"path": "synthetic-catalog.json", "sha256": "a" * 64}):
            with self.assertRaises(gate.Invalid): validator.evidence(reference)
        (self.root / "linked.json").symlink_to(self.catalog_path)
        with self.assertRaisesRegex(gate.Invalid, "symlink"):
            validator.evidence({"path": "linked.json", "sha256": self.catalog_ref["sha256"]})

    def test_source_report_cannot_hide_skipped_tests(self):
        obligation = next(row for row in self.receipt["historicalObligations"] if row["id"] == "RD-B5-MCP-001")
        identity = obligation["cases"]["postgresql_regression"]
        row = self.read(self.receipt["cases"][identity])
        report = self.root / row["testResults"]["path"]
        report.write_text('<TestRun><Results><UnitTestResult outcome="NotExecuted"/></Results></TestRun>')
        row["testResults"]["sha256"] = gate.sha256(report)
        self.receipt["cases"][identity] = self.write(row)
        with self.assertRaisesRegex(gate.Invalid, "failed/skipped"):
            self.validator().validate()

    def test_duplicate_json_properties_are_not_silently_replaced(self):
        path = self.root / "duplicate.json"
        path.write_text('{"readiness":"NOT_RUN","readiness":"PASS"}')
        with self.assertRaisesRegex(gate.Invalid, "duplicate JSON"):
            gate.read_json(path)


if __name__ == "__main__":
    unittest.main()

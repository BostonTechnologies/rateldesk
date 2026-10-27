"""Synthetic offline tests for beta.12 campaign preparation and unchanged final gates."""

import copy
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest


ROOT = Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


gate = load("beta12_campaign_test_gate", ROOT / "tools/ci/validate-beta12-campaign.py")


class Beta12CampaignTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="rateldesk-beta12-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.sequence = 0

    def write(self, value):
        self.sequence += 1
        path = self.root / f"beta12-capture-{self.sequence:05d}.json"
        path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
        return {"path": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}

    def catalog(self):
        identities = set(gate.read_json(ROOT / "tools/ci/beta10-operation-ids.json")["operationIds"])
        identities.add("helpdesk_email_settings.delete_rule")
        operations, groups = [], {}
        for identity in sorted(identities):
            tool, name = identity.split(".", 1)
            operations.append({"tool": tool, "operation": name, "readOnly": True, "confirmationRequired": False})
            groups.setdefault(tool, []).append(name)
        schemas = [{"tool": tool, "inputSchema": {"type": "object", "properties": {
            "operation": {"type": "string", "enum": names}}}} for tool, names in sorted(groups.items())]
        document = {"catalogRevision": gate.CATALOG_REVISION, "operations": operations,
                    "operationCount": len(operations), "toolSchemas": schemas,
                    "catalogHash": gate.legacy.native_hash(operations), "schemaHash": gate.legacy.native_hash(schemas)}
        reference = self.write(document)
        return self.root / reference["path"]

    def fixture(self):
        builder = load("beta12_synthetic_fixture_builder", ROOT / "tools/ci/test-beta8-campaign.py")
        builder.gate = SimpleNamespace(**{name: getattr(gate.legacy, name)
                                          for name in dir(gate.legacy) if not name.startswith("_")})
        builder.gate.Validator = gate.Validator
        fixture = builder.CampaignGateTests()
        fixture.root, fixture.sequence = self.root, 0
        fixture.source, fixture.now = "a" * 40, datetime(2026, 10, 19, 2, tzinfo=timezone.utc)
        fixture.catalog_path = self.catalog()
        fixture.catalog = gate.read_json(fixture.catalog_path)
        fixture.catalog_ref = {"path": fixture.catalog_path.name, "sha256": gate.legacy.sha256(fixture.catalog_path)}
        fixture.ops = {f"{row['tool']}.{row['operation']}": row for row in fixture.catalog["operations"]}
        fixture.prepare_receipt()
        return fixture

    def ready_prerequisites(self, context):
        result = gate.prerequisite_template()
        result.update(context=context, campaignStartedAt="2026-10-19T00:10:00Z", authenticationMode="Local")
        for row in result["items"]:
            row.update(state="READY", observedAt="2026-10-19T00:10:05Z")
            facts = {item["id"]: True for item in row["assertions"]}
            reference = self.write({"unitTestOnly": True, "context": context,
                                    "observedAt": "2026-10-19T00:10:04Z", "facts": facts})
            for item in row["assertions"]:
                item.update(evidence=reference, pointer="/facts/" + item["id"], expected=True)
        return result

    def boundary(self, fixture, limit=30_000_000):
        configuration = self.write({"unitTestOnly": True, "kind": "effective_deployment_configuration",
                                    "context": fixture.context, "observedAt": "2026-10-19T00:10:04Z",
                                    "effectiveMaximumBodyBytes": limit})
        fixture.receipt["transportLimitConfiguration"] = self.write({
            "context": fixture.context, "observedAt": "2026-10-19T00:10:05Z", "maximumBodyBytes": limit,
            "components": {name: {"evidence": configuration, "pointer": "/effectiveMaximumBodyBytes"}
                           for name in ("web", "mcp-http")}})
        summary = {"format": "rateldesk-beta12-body-boundary/v1", "completed": True,
                   "version": gate.VERSION, "sourceRevision": fixture.source,
                   "maximumBodyBytes": limit, "startedAt": "2026-10-19T00:10:10Z",
                   "completedAt": "2026-10-19T00:10:16Z", "cases": []}
        for key, at in (("identityBefore", "2026-10-19T00:10:11Z"), ("identityAfter", "2026-10-19T00:10:15Z")):
            summary[key] = self.write({"observedAt": at, "body": {"version": gate.VERSION, "commitHash": fixture.source}})
        matrix = [(path, framing, size) for path in ("/mcp", "/mcp/")
                  for framing in ("content-length", "chunked") for size in (limit - 1, limit, limit + 1)]
        if limit == 30_000_000:
            matrix.append(("/mcp/", "chunked", 29_990_208))
        for number, (path, framing, size) in enumerate(matrix):
            rpc_id = f"synthetic-beta12-{number}"
            request = {"observedAt": "2026-10-19T00:10:12Z", "path": path, "framing": framing,
                       "decodedBodyBytes": size, "rpcId": rpc_id, "method": "initialize",
                       "bodySha256": hashlib.sha256(rpc_id.encode()).hexdigest(),
                       "uploadBehavior": "send-complete-body-before-reading-response"}
            body = ({"id": rpc_id, "result": {"serverInfo": {"name": "unit-test"}}} if size <= limit else
                    {"status": 413, "code": "request_too_large", "limit": "body", "maximumBodyBytes": limit})
            response = {"observedAt": "2026-10-19T00:10:13Z", "httpStatus": 200 if size <= limit else 413,
                        "contentType": "application/json" if size <= limit else "application/problem+json", "body": body}
            summary["cases"].append({"path": path, "framing": framing, "decodedBodyBytes": size,
                                     "request": self.write(request), "response": self.write(response)})
        fixture.receipt["transportBoundary"] = self.write(summary)
        return summary

    def add_effects(self, fixture):
        mail = fixture.operation("helpdesk_mail.send")["positiveCaseId"]
        support = next(row for row in fixture.receipt["scenarios"] if row["id"] == "support")
        approval = support["cases"]["concurrent_approval_decisions"]
        for identity in (mail, approval):
            row = fixture.read(fixture.receipt["cases"][identity])
            scope = {"campaignId": fixture.context["campaignId"], "correlation": identity}
            before = {"scope": scope, "observedAt": "2026-10-19T00:12:00Z", "effects": []}
            first = {"scope": scope, "observedAt": "2026-10-19T00:13:00Z",
                     "effects": [{"id": "owned-effect", "effectKey": "owned-effect-key"}]}
            row["effectCardinality"] = {"expectedNewEffects": 1, "before": self.write(before), "first": self.write(first)}
            if identity == approval:
                row["effectCardinality"]["replay"] = self.write(dict(first, observedAt="2026-10-19T00:14:00Z"))
            else:
                row["postcondition"] = fixture.observation(["direct_send_delivery_matches_effect"], ("storage", "transport_capture"))
            fixture.receipt["cases"][identity] = fixture.write(row)

    @staticmethod
    def prepared_validator(fixture):
        validator = fixture.validator()
        validator.provenance()
        validator.identities()
        return validator

    def test_fresh_workspace_preserves_denominators_and_historical_modules(self):
        path = self.root / "fresh"
        result = gate.initialize(path, self.catalog())
        self.assertEqual((390, 103, 86, 9, 12), tuple(result[key] for key in
            ("operations", "domainCases", "historicalObligations", "cleanupAssertions", "residualFixtureLanes")))
        receipt = gate.read_json(path / "receipt.json")
        self.assertEqual((gate.FORMAT, gate.VERSION, "NOT_RUN"),
                         (receipt["format"], receipt["campaign"]["version"], receipt["readiness"]))
        self.assertTrue(all(row["state"] == "NOT_RUN" for row in receipt["operations"]))
        self.assertEqual({}, receipt["cases"])
        self.assertIsNone(receipt["prerequisites"])
        self.assertEqual(59, sum(len(row["operationIds"]) for row in gate.LANES.values()))
        with self.assertRaises(gate.Invalid):
            gate.initialize(path, self.catalog())
        historical = load("beta11_unmodified_test_import", ROOT / "tools/ci/validate-beta11-campaign.py")
        self.assertEqual("0.1.2-beta.11", historical.VERSION)
        self.assertEqual("0.1.2-beta.11", historical.legacy.VERSION)

    def test_basic_mail_and_chat_ready_does_not_hide_advanced_fixtures(self):
        fixture = self.fixture()
        prerequisites = self.ready_prerequisites(fixture.context)
        for row in prerequisites["items"]:
            if row["id"] in gate.LANES:
                row["state"] = "NOT_CHECKED"
        result = gate.check_prerequisites(prerequisites, self.prepared_validator(fixture))
        self.assertFalse(result["ready"])
        self.assertEqual(set(gate.LANES), {row["id"] for row in result["missing"]})
        self.assertEqual(59, sum(len(row["affectedOperations"]) for row in result["missing"]))

    def test_ready_requires_current_true_captures_and_complete_assertions(self):
        fixture = self.fixture()
        original = self.ready_prerequisites(fixture.context)
        for problem in ("false_fact", "old_context", "stale", "missing_assertion", "changed_operations"):
            with self.subTest(problem=problem):
                data = copy.deepcopy(original)
                row = next(item for item in data["items"] if item["id"] == "mail_recovery")
                if problem == "missing_assertion":
                    row["assertions"].pop()
                elif problem == "changed_operations":
                    row["affectedOperations"].pop()
                else:
                    assertion = row["assertions"][0]
                    capture = gate.read_json(self.root / assertion["evidence"]["path"])
                    if problem == "false_fact":
                        capture["facts"][assertion["id"]] = False
                    elif problem == "old_context":
                        capture["context"]["version"] = "0.1.2-beta.11"
                    else:
                        capture["observedAt"] = "2026-10-18T00:10:04Z"
                    assertion["evidence"] = self.write(capture)
                with self.assertRaises(gate.Invalid):
                    gate.check_prerequisites(data, self.prepared_validator(fixture))

    def test_default_boundary_requires_effective_configuration_and_eager_upload(self):
        fixture = self.fixture()
        summary = self.boundary(fixture)
        self.prepared_validator(fixture).transport_boundary()
        config = fixture.receipt["transportLimitConfiguration"]
        fixture.receipt["transportLimitConfiguration"] = None
        with self.assertRaises(gate.Invalid):
            self.prepared_validator(fixture).transport_boundary()
        fixture.receipt["transportLimitConfiguration"] = config
        request = gate.read_json(self.root / summary["cases"][0]["request"]["path"])
        del request["uploadBehavior"]
        summary["cases"][0]["request"] = self.write(request)
        fixture.receipt["transportBoundary"] = self.write(summary)
        with self.assertRaisesRegex(gate.Invalid, "eager-upload"):
            self.prepared_validator(fixture).transport_boundary()

    def test_reset_legacy_summary_and_wrong_media_type_cannot_pass(self):
        fixture = self.fixture()
        original = self.boundary(fixture, 4096)
        self.prepared_validator(fixture).transport_boundary()
        for problem in ("reset", "old_format", "wrong_media", "missing_media", "null_media", "non_string_media"):
            with self.subTest(problem=problem):
                summary = copy.deepcopy(original)
                if problem == "old_format":
                    summary["format"] = "rateldesk-beta11-body-boundary/v1"
                else:
                    row = next(item for item in summary["cases"] if item["decodedBodyBytes"] == 4097)
                    response = gate.read_json(self.root / row["response"]["path"])
                    if problem == "reset":
                        response = {"observedAt": response["observedAt"], "transportErrorType": "ConnectionResetError"}
                    elif problem == "missing_media":
                        response.pop("contentType")
                    else:
                        response["contentType"] = {"wrong_media": "text/html", "null_media": None, "non_string_media": 413}[problem]
                    row["response"] = self.write(response)
                fixture.receipt["transportBoundary"] = self.write(summary)
                with self.assertRaises(gate.Invalid):
                    self.prepared_validator(fixture).transport_boundary()

    def test_partial_mapping_plan_only_offers_valid_current_assertions_and_never_edits(self):
        fixture = self.fixture()
        self.add_effects(fixture)
        obligation = fixture.receipt["historicalObligations"][0]
        slot = next(iter(obligation["cases"]))
        identity = obligation["cases"][slot]
        obligation["cases"][slot] = None
        fixture.receipt.update(readiness="NOT_READY", productBlockers=["synthetic-open-gate"], unresolvedFailures=1)
        fixture.receipt["campaign"].update(completedAt=None, functionalCompletedAt=None)
        original = copy.deepcopy(fixture.receipt)
        validator = gate.planning_validator(fixture.receipt, self.root, fixture.catalog_path, fixture.manifest_path, fixture.now)
        result = gate.mapping_plan(validator)
        target = next(row for row in result["mappings"] if row["section"] == "historicalObligations"
                      and row["id"] == obligation["id"] and row["slot"] == slot)
        self.assertIn(identity, target["candidateCaseIds"])
        self.assertEqual(f"historical:{obligation['id']}/{slot}", target["required"]["assertion"])
        self.assertEqual("NOT_VALIDATED", result["readiness"])
        self.assertFalse(result["receiptModified"])
        self.assertEqual(original, fixture.receipt)
        fixture.change_case(identity, lambda row: row.update(context=dict(fixture.context, version="0.1.2-beta.11")))
        result = gate.mapping_plan(gate.planning_validator(fixture.receipt, self.root, fixture.catalog_path, fixture.manifest_path, fixture.now))
        self.assertIn(identity, {row["caseId"] for row in result["invalidCases"]})
        self.assertTrue(all(identity not in row["candidateCaseIds"] for row in result["mappings"]))

    def test_mapping_plan_still_requires_exact_release_identity(self):
        fixture = self.fixture()
        fixture.receipt["campaign"]["sourceRevision"] = "b" * 40
        with self.assertRaisesRegex(gate.Invalid, "source revision"):
            gate.planning_validator(fixture.receipt, self.root, fixture.catalog_path, fixture.manifest_path, fixture.now)

    def test_complete_gate_retains_all_requirements_and_adds_granular_readiness(self):
        fixture = self.fixture()
        self.add_effects(fixture)
        self.boundary(fixture)
        prerequisites = self.ready_prerequisites(fixture.context)
        fixture.receipt["prerequisites"] = self.write(prerequisites)
        result = fixture.validator().validate()
        self.assertEqual((383, 103, 86, 900), tuple(result[key] for key in
                         ("operationsPassed", "scenarioCases", "historicalObligations", "soakSeconds")))
        self.assertTrue(result["granularPrerequisitesValidated"])
        prerequisites["items"][-1]["state"] = "NOT_CHECKED"
        fixture.receipt["prerequisites"] = self.write(prerequisites)
        with self.assertRaisesRegex(gate.Invalid, "granular"):
            fixture.validator().validate()


if __name__ == "__main__":
    unittest.main()

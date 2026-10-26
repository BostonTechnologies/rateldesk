"""Synthetic rejection tests for the current gate; never live acceptance evidence."""

import copy
import hashlib
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("beta11_gate_tests", ROOT / "tools/ci/validate-beta11-campaign.py")
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)


class Beta11CampaignTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.sequence = 0

    def write(self, data):
        self.sequence += 1
        path = self.root / f"capture-{self.sequence}.json"
        path.write_text(json.dumps(data) + "\n")
        return {"path": path.name, "sha256": gate.legacy.sha256(path)}

    def catalog(self, omit=None):
        ids = set(gate.read_json(gate.BASELINE)["operationIds"]) | {"helpdesk_email_settings.delete_rule"}
        ids.discard(omit)
        operations, tools = [], {}
        for identity in sorted(ids):
            tool, operation = identity.split(".", 1)
            operations.append({"tool": tool, "operation": operation, "confirmationRequired": False, "readOnly": True})
            tools.setdefault(tool, []).append(operation)
        schemas = [{"tool": tool, "inputSchema": {"properties": {"operation": {"enum": names}}}}
                   for tool, names in tools.items()]
        data = {"catalogRevision": gate.CATALOG_REVISION, "operationCount": len(ids),
                "operations": operations, "toolSchemas": schemas,
                "catalogHash": gate.legacy.native_hash(operations), "schemaHash": gate.legacy.native_hash(schemas)}
        return self.root / self.write(data)["path"]

    def test_current_workspace_has_all_denominators_and_no_inherited_passes(self):
        catalog = self.catalog()
        directory = self.root / "fresh"
        result = gate.initialize(directory, catalog)
        self.assertEqual((result["operations"], result["domainCases"], result["historicalObligations"]), (390, 103, 86))
        receipt = gate.read_json(directory / "receipt.json")
        self.assertEqual(receipt["readiness"], "NOT_RUN")
        self.assertTrue(all(row["state"] == "NOT_RUN" for row in receipt["operations"]))
        progress = gate.progress(receipt, catalog)
        self.assertEqual(len(progress["operationsNeedingEvidence"]), 390)
        self.assertEqual(progress["readiness"], "NOT_VALIDATED")
        with self.assertRaises(gate.Invalid):
            gate.initialize(directory, catalog)

    def test_old_operation_cannot_be_silently_removed(self):
        with self.assertRaises(gate.Invalid):
            gate.export_catalog(self.catalog(omit="helpdesk_incidents.get"))

    def test_recorded_pass_without_capture_mapping_remains_incomplete(self):
        catalog = self.catalog()
        receipt = gate.legacy.template(catalog)
        for row in receipt["operations"]:
            row["state"] = "PASS"
        progress = gate.progress(receipt, catalog)
        self.assertEqual(len(progress["operationsNeedingEvidence"]), 390)

    def snapshots(self):
        scope = {"campaignId": "synthetic-campaign", "correlation": "synthetic-owned-approval"}
        before = {"observedAt": "2026-10-25T00:00:01Z", "scope": scope, "effects": []}
        first = {"observedAt": "2026-10-25T00:00:02Z", "scope": scope,
                 "effects": [{"id": "synthetic-effect", "effectKey": "synthetic-key"}]}
        replay = dict(copy.deepcopy(first), observedAt="2026-10-25T00:00:03Z")
        return before, first, replay

    def test_approval_effect_sets_are_compared_not_only_counts(self):
        before, first, replay = self.snapshots()
        self.assertEqual(gate.effect_cardinality(before, first, 1, replay)["replayAddedEffects"], 0)
        replay["effects"][0]["id"] = "replacement-effect"
        with self.assertRaises(gate.Invalid):
            gate.effect_cardinality(before, first, 1, replay)

    def test_effect_scope_duplication_and_unobserved_first_effect_fail(self):
        for change in ("scope", "duplicate", "missing"):
            before, first, replay = self.snapshots()
            if change == "scope":
                replay["scope"]["correlation"] = "another-fixture"
            elif change == "duplicate":
                replay["effects"].append({"id": "second", "effectKey": "synthetic-key"})
            else:
                first["effects"] = []
            with self.assertRaises(gate.Invalid):
                gate.effect_cardinality(before, first, 1, replay)

    def boundary(self):
        validator = object.__new__(gate.Validator)
        validator.root, validator.files, validator.bytes = self.root, {}, 0
        validator.context = {"sourceRevision": "a" * 40}
        validator.started = datetime(2026, 10, 25, tzinfo=timezone.utc)
        validator.completed = datetime(2026, 10, 25, 1, tzinfo=timezone.utc)
        validator.functional = validator.completed
        limit = 30_000_000
        summary = {"format": "rateldesk-beta11-body-boundary/v1", "completed": True,
            "version": gate.VERSION, "sourceRevision": "a" * 40, "maximumBodyBytes": limit,
            "startedAt": "2026-10-25T00:00:00Z", "completedAt": "2026-10-25T00:00:06Z", "cases": []}
        for key, at in (("identityBefore", "2026-10-25T00:00:01Z"), ("identityAfter", "2026-10-25T00:00:05Z")):
            summary[key] = self.write({"observedAt": at, "body": {"version": gate.VERSION, "commitHash": "a" * 40}})
        rows = [(path, framing, size) for path in ("/mcp", "/mcp/") for framing in ("content-length", "chunked")
                for size in (limit - 1, limit, limit + 1)] + [("/mcp/", "chunked", 29_990_208)]
        for number, (path, framing, size) in enumerate(rows):
            rpc_id = "synthetic-" + str(number)
            request = {"path": path, "framing": framing, "decodedBodyBytes": size, "rpcId": rpc_id,
                "method": "initialize", "observedAt": "2026-10-25T00:00:02Z",
                "bodySha256": hashlib.sha256(rpc_id.encode()).hexdigest()}
            response = {"observedAt": "2026-10-25T00:00:03Z", "httpStatus": 200 if size <= limit else 413,
                "body": {"id": rpc_id, "result": {"serverInfo": {"name": "synthetic"}}} if size <= limit else {
                    "status": 413, "code": "request_too_large", "limit": "body", "maximumBodyBytes": limit}}
            summary["cases"].append({"path": path, "framing": framing, "decodedBodyBytes": size,
                "request": self.write(request), "response": self.write(response)})
        validator.data = {"transportBoundary": self.write(summary)}
        return validator, summary

    def test_full_boundary_matrix_passes_but_one_missing_route_case_fails(self):
        validator, summary = self.boundary()
        validator.transport_boundary()
        summary["cases"].pop(0)
        validator.data["transportBoundary"] = self.write(summary)
        with self.assertRaises(gate.Invalid):
            validator.transport_boundary()

    def test_connection_reset_cannot_be_relabelled_as_successful_oversize_rejection(self):
        validator, summary = self.boundary()
        oversized = next(row for row in summary["cases"] if row["decodedBodyBytes"] > 30_000_000)
        oversized["response"] = self.write({"observedAt": "2026-10-25T00:00:03Z", "transportErrorType": "ConnectionResetError"})
        validator.data["transportBoundary"] = self.write(summary)
        with self.assertRaises(gate.Invalid):
            validator.transport_boundary()

    def isolation(self):
        validator = object.__new__(gate.Validator)
        validator.root, validator.files, validator.bytes, validator.case_cache = self.root, {}, 0, {}
        validator.context = {"campaignId": "synthetic-campaign"}
        validator.started = datetime(2026, 10, 25, tzinfo=timezone.utc)
        validator.completed = datetime(2026, 10, 25, 1, tzinfo=timezone.utc)
        validator.functional = validator.completed
        validator.actors = {"owner": {"kind": "owner"}, "ordinary": {"kind": "scoped"}}
        notification = {"id": "00000000-0000-0000-0000-000000000001", "readAt": None}
        before = self.write({"observedAt": "2026-10-25T00:00:01Z", "body": notification})
        after = self.write({"observedAt": "2026-10-25T00:00:03Z", "body": notification})
        request = self.write({"jsonrpc": "2.0", "id": "synthetic-rpc", "method": "tools/call", "params": {
            "name": "helpdesk_notifications", "arguments": {"operation": "mark_read", "confirm": True,
            "request": {"ids": [notification["id"]]}}}})
        response = self.write({"jsonrpc": "2.0", "id": "synthetic-rpc", "result": {"structuredContent": {
            "success": True, "status": "completed", "data": {"updated": 0}}}})
        row = {"id": "isolation", "operation": "helpdesk_notifications.mark_read", "actor": "ordinary",
            "kind": "expected_negative", "outcome": "PASS", "origin": "fresh_deployed", "transport": "http",
            "context": validator.context, "startedAt": "2026-10-25T00:00:00Z", "completedAt": "2026-10-25T00:00:05Z",
            "effectObserved": False, "request": request, "response": response,
            "isolationReadback": {"ownerActor": "owner", "before": before, "after": after},
            "postcondition": {"observedAt": "2026-10-25T00:00:04Z", "independent": True, "assertions": [
                {"id": "other_actor_read_marker_unchanged", "surface": "api", "evidence": after,
                 "pointer": "/body/readAt", "expected": None},
                {"id": "stored_marker_unchanged", "surface": "storage", "evidence": after,
                 "pointer": "/body/readAt", "expected": None}]}}
        validator.data = {"cases": {"isolation": self.write(row)}}
        return validator, row

    def test_zero_update_isolation_keeps_wire_response_and_cannot_be_positive(self):
        validator, row = self.isolation()
        result = validator.case("isolation", "expected_negative")
        self.assertEqual(result["wireStatus"], "completed")
        original = validator.evidence(row["response"])
        self.assertIs(original["result"]["structuredContent"]["success"], True)
        with self.assertRaises(gate.Invalid):
            validator.case("isolation", "positive")

    def test_zero_update_without_exact_independent_readbacks_fails(self):
        for change in ("same_capture", "wrong_owner", "missing_storage", "wrong_record"):
            validator, row = self.isolation()
            if change == "same_capture":
                row["isolationReadback"]["after"] = row["isolationReadback"]["before"]
            elif change == "wrong_owner":
                row["isolationReadback"]["ownerActor"] = "ordinary"
            elif change == "missing_storage":
                row["postcondition"]["assertions"].pop()
            else:
                after = validator.evidence(row["isolationReadback"]["after"])
                after["body"]["readAt"] = "2026-10-25T00:00:02Z"
                row["isolationReadback"]["after"] = self.write(after)
            validator.data["cases"]["isolation"] = self.write(row)
            with self.assertRaises(gate.Invalid):
                validator.case("isolation", "expected_negative")

    def test_prerequisite_template_is_incomplete_and_false_facts_cannot_be_ready(self):
        data = gate.prerequisite_template()
        data["authenticationMode"] = "Local"
        result = gate.check_prerequisites(data, self.root)
        self.assertFalse(result["ready"])
        self.assertEqual(len(result["missing"]), len(gate.PREREQUISITES))
        row = data["items"][0]
        row["state"], row["observedAt"] = "READY", "2026-10-24T00:00:00Z"
        for assertion in row["assertions"]:
            assertion.update(evidence=self.write({"ready": False}), pointer="/ready", expected=False)
        with self.assertRaises(gate.Invalid):
            gate.check_prerequisites(data, self.root)

    def test_complete_current_synthetic_receipt_and_missing_new_effect_proof(self):
        # Reuse the immutable gate's explicitly synthetic fixture builder to
        # exercise its entire inherited gate with current release bindings.
        spec = importlib.util.spec_from_file_location("beta11_synthetic_fixture_builder", ROOT / "tools/ci/test-beta8-campaign.py")
        builder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(builder)
        builder.gate = SimpleNamespace(**{name: value for name, value in vars(gate.legacy).items() if not name.startswith("__")})
        builder.gate.Validator = gate.Validator
        fixture = builder.CampaignGateTests()
        fixture.root, fixture.sequence, fixture.source = self.root, 0, "a" * 40
        fixture.now = datetime(2026, 10, 25, 1, tzinfo=timezone.utc)
        fixture.catalog_path = self.catalog()
        fixture.catalog = gate.read_json(fixture.catalog_path)
        fixture.catalog_ref = {"path": fixture.catalog_path.name, "sha256": gate.legacy.sha256(fixture.catalog_path)}
        fixture.ops = {row["tool"] + "." + row["operation"]: row for row in fixture.catalog["operations"]}
        fixture.prepare_receipt()

        _, boundary = self.boundary()
        def redate(value):
            if isinstance(value, dict):
                if set(value) == {"path", "sha256"}:
                    return fixture.write(redate(gate.read_json(self.root / value["path"])))
                return {name: redate(item) for name, item in value.items()}
            if isinstance(value, list):
                return [redate(item) for item in value]
            if isinstance(value, str):
                return value.replace("2026-10-25T00:00:", "2026-10-19T00:11:")
            return value
        fixture.receipt["transportBoundary"] = fixture.write(redate(boundary))

        def add_effects(case_id, include_replay):
            row = fixture.read(fixture.receipt["cases"][case_id])
            snapshots = self.snapshots()
            for snapshot, minute in zip(snapshots, (12, 13, 14)):
                snapshot["observedAt"] = f"2026-10-19T00:{minute}:00Z"
                snapshot["scope"]["campaignId"] = fixture.context["campaignId"]
            row["effectCardinality"] = {"expectedNewEffects": 1,
                "before": fixture.write(snapshots[0]), "first": fixture.write(snapshots[1])}
            if include_replay:
                row["effectCardinality"]["replay"] = fixture.write(snapshots[2])
            else:
                row["postcondition"] = fixture.observation(["direct_send_delivery_matches_effect"], ("storage", "transport_capture"))
            fixture.receipt["cases"][case_id] = fixture.write(row)
        mail_case = fixture.operation("helpdesk_mail.send")["positiveCaseId"]
        add_effects(mail_case, False)
        support = next(row for row in fixture.receipt["scenarios"] if row["id"] == "support")
        add_effects(support["cases"]["concurrent_approval_decisions"], True)
        result = fixture.validator().validate()
        self.assertEqual(result["version"], "0.1.2-beta.11")
        self.assertEqual(result["operationsPassed"], 383)
        self.assertEqual((result["scenarioCases"], result["historicalObligations"]), (103, 86))
        self.assertTrue(result["httpBodyBoundaryValidated"])
        fixture.change_case(mail_case, lambda row: row.pop("effectCardinality"))
        with self.assertRaises(gate.Invalid):
            fixture.validator().validate()


if __name__ == "__main__":
    unittest.main()

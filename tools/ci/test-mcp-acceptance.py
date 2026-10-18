#!/usr/bin/env python3
"""Exercise omission and false-closure risks using synthetic offline receipts."""

import copy
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


SPEC = importlib.util.spec_from_file_location("mcp_acceptance_gate", Path(__file__).with_name("validate-mcp-acceptance.py"))
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)


class AcceptanceGateTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="rateldesk-acceptance-gate-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.register = gate.build_template()
        self.now = datetime(2026, 10, 18, 2, tzinfo=timezone.utc)
        self.counter = 0

    def write(self, value, name=None):
        self.counter += 1
        name = name or f"receipt-{self.counter:05d}.json"
        content = (json.dumps(value, indent=2) + "\n").encode()
        (self.directory / name).write_bytes(content)
        return {"path": name, "sha256": hashlib.sha256(content).hexdigest()}

    def change_receipt(self, reference, **changes):
        value = gate.read_json(self.directory / reference["path"])
        value.update(changes)
        return self.write(value, reference["path"])

    def validator(self):
        return gate.Validator(self.register, self.directory, self.now)

    def acknowledge(self, identity, role, name):
        return self.write({"obligationId": identity, "role": role, "name": name, "acknowledged": True,
                           "acknowledgedAt": "2026-10-18T00:00:00Z", "targetDeliveryAt": "2026-10-18T01:00:00Z"})

    def make_ready(self, export_transform=None):
        """Construct test-only claims; no supplied or deployed evidence is used."""
        campaign = self.register["campaign"]
        source = hashlib.sha1(b"synthetic candidate source").hexdigest()
        campaign.update({"id": "synthetic-campaign", "startedAt": "2026-10-18T00:10:00Z",
                         "completedAt": "2026-10-18T01:00:00Z", "sourceRevision": source})
        observation = self.write({"syntheticFixture": True, "meaning": "unit test evidence only"})
        assets, components, containers, natives = {}, {}, [], {}
        for component, image_family in gate.IMAGE_FAMILIES.items():
            image = f"{image_family}:{gate.VERSION}"
            digest = "sha256:" + hashlib.sha256(component.encode()).hexdigest()
            containers.append({"image": image, "digest": digest})
            components[component] = {"version": gate.VERSION, "sourceRevision": source, "digest": digest, "observation": observation}
        for tool in ("cli", "mcp-stdio"):
            for rid in ("linux-x64", "linux-arm64", "win-x64"):
                name = f"rateldesk-{tool}-{gate.VERSION}-{rid}.{'zip' if rid == 'win-x64' else 'tar.gz'}"
                assets[name] = hashlib.sha256(name.encode()).hexdigest()
                natives[name] = {"sha256": assets[name], "version": gate.VERSION, "sourceRevision": source, "observation": observation}
        campaign["releaseManifest"] = self.write({"version": gate.VERSION, "sourceRevision": source, "tag": "v" + gate.VERSION,
                                                  "containers": containers, "assets": assets})
        campaign["deployment"] = self.write({"campaignId": campaign["id"], "observedAt": "2026-10-18T00:10:01Z",
                                               "components": components, "nativeTools": natives})
        export = gate.read_json(gate.ROOT / "docs/contracts/mcp-operation-catalog.json")
        if export_transform:
            export_transform(export)
        wire = {"result": {"tools": [{"name": row["tool"], "inputSchema": row["inputSchema"]} for row in export["toolSchemas"]]}}
        current = {f"{row['tool']}.{row['operation']}": row for row in export["operations"]}
        baseline = {f"{row['tool']}.{row['operation']}": row for row in gate.read_json(gate.BASELINE_CATALOG)["operations"]}
        campaign["catalog"] = self.write({"campaignId": campaign["id"], "observedAt": "2026-10-18T00:10:02Z",
            "wireDiscovery": self.write(wire), "exportedCatalog": self.write(export),
            "semanticCatalogHash": export["catalogHash"], "schemaHash": export["schemaHash"],
            "semanticHashObservation": observation, "addedOperations": sorted(set(current) - set(baseline)), "removedOperations": [],
            "operationChanges": {key: "added" if key not in baseline else "unchanged" if row == baseline[key] else "changed" for key, row in current.items()}})
        context = {"campaignId": campaign["id"], "version": gate.VERSION, "sourceRevision": source,
                   "deploymentSha256": campaign["deployment"]["sha256"], "catalogReceiptSha256": campaign["catalog"]["sha256"]}
        self.context, self.observation = context, observation
        for row in self.register["dependencies"]:
            row.update({"state": "DEMONSTRATED", "owner": "Dependency Owner Alpha", "demonstratedAt": "2026-10-18T00:09:00Z",
                "acknowledgement": self.acknowledge(row["id"], "prerequisite", "Dependency Owner Alpha"),
                "evidence": self.write({"dependencyId": row["id"], "demonstratedAt": "2026-10-18T00:09:00Z",
                    "demonstrated": True, "deployedTargetVerified": True, "campaignId": campaign["id"],
                    "sourceRevision": source, "observation": observation})})
            if row["id"] == "original_case_corpus":
                row["evidence"] = self.change_receipt(row["evidence"], immutableSources=[observation],
                    caseManifest=self.write({"cases": [{"id": "original-case-01", "executionKind": "positive", "operation": "helpdesk_health.get"}]}))
        for row in self.register["obligations"]:
            row["state"] = "CLOSED"
            row["workStartedAt"] = "2026-10-18T00:05:00Z"
            for role in gate.ROLES:
                name = "Review Owner Beta" if role == "verifier" else "Delivery Owner Alpha"
                row["owners"][role] = {"name": name, "acknowledgedAt": "2026-10-18T00:00:00Z",
                                       "acknowledgement": self.acknowledge(row["id"], role, name)}
            row["implementationEvidence"] = [observation]
            for case_id, kind in row["cases"].items():
                row["caseEvidence"][case_id] = self.make_case(row["id"], case_id, kind, row["operation"])
            row["closure"] = self.write({"obligationId": row["id"], "verifier": "Review Owner Beta", "independentlyClosed": True,
                                          "context": context, "closedAt": "2026-10-18T00:50:00Z"})
        for operation in current:
            if operation in gate.UNSUPPORTED:
                campaign["operations"].append({"id": operation, "state": "UNSUPPORTED_BY_DESIGN",
                                                "reason": "HTTP remote configuration persistence is disabled"})
            else:
                campaign["operations"].append({"id": operation, "state": "PASS",
                                                "evidence": self.make_case("catalog", operation, "positive", operation)})
        campaign["historicalCases"] = [{"id": "original-case-01", "state": "PASS",
            "evidence": self.make_case("historical-corpus", "original-case-01", "positive", "helpdesk_health.get")}]
        samples = [{"at": "2026-10-18T00:25:00Z", "latencyMs": 10, "unexpectedFailure": False},
                   {"at": "2026-10-18T00:39:59Z", "latencyMs": 12, "unexpectedFailure": False}]
        campaign["soak"] = self.write({"context": context, "startedAt": "2026-10-18T00:25:00Z", "completedAt": "2026-10-18T00:40:00Z",
            "afterFunctionalTests": True, "healthy": True, "recoveryVerified": True, "totalRequests": len(samples),
            "samples": self.write(samples), "unexpectedFailures": 0, "unresolvedFailures": 0, "workloadAndRecoveryEvidence": observation})
        campaign["restoration"] = self.write({"context": context, "complete": True, "verifier": "Review Owner Beta",
            "verifiedAt": "2026-10-18T00:55:00Z", "evidence": observation, "pausedWorkerPreserved": True,
            "savedSenderDisabled": True, "unrelatedQueuesPreserved": True, "terminalHistoryPreserved": True,
            "temporarySettingsRestored": True, "campaignJobsStopped": True, "uncertainOutcomesReconciled": True,
            "ownedFixturesRetained": True})
        campaign["independentReview"] = self.write({"context": context, "complete": True, "verifier": "Review Owner Beta",
            "verifiedAt": "2026-10-18T00:56:00Z", "evidence": observation, "acceptanceIssue": gate.ISSUES + "219",
            "allOriginalCasesReconciled": True, "unresolvedReproducedDefects": 0, "missingMandatoryDependencies": 0,
            "ownerReceivedCompleteReceipt": True})
        self.register["productionReadiness"] = "YES"

    def make_case(self, identity, case_id, kind, operation):
        early = identity.startswith("B6-R")
        return self.write({"obligationId": identity, "caseId": case_id, "outcome": "PASS", "executionKind": kind,
            "executor": "Campaign Operator Gamma", "verifier": "Review Owner Beta", "context": self.context,
            "sourceRevision": self.context["sourceRevision"], "origin": "fresh_deployed", "transport": "http",
            "startedAt": "2026-10-18T00:11:00Z" if early else "2026-10-18T00:13:00Z",
            "completedAt": "2026-10-18T00:12:00Z" if early else "2026-10-18T00:20:00Z",
            "operation": operation, "confirmedExecution": True, "mutationObserved": True, "supportedPositive": True,
            "previewImmutable": True, "postconditionSurface": "storage", "identityEvidence": self.observation,
            "wireEvidence": self.observation, "postconditionEvidence": self.observation, "restorationEvidence": self.observation,
            "transportReceiptEvidence": self.observation, "passedTests": 1, "failedTests": 0, "skippedTests": 0,
            "testResults": self.observation, "latencyBudget": {"metric": "p95", "budgetMs": 1500, "measuredMs": 300,
                "agreedAt": "2026-10-18T00:00:00Z", "agreementEvidence": self.observation, "workloadEvidence": self.observation}})

    def test_public_template_is_structurally_valid_but_acceptance_stays_open(self):
        checked_in = gate.read_json(gate.DEFAULT_REGISTER)
        validator = gate.Validator(checked_in)
        validator.structure()
        self.assertEqual(59, sum(row["kind"] == "historical_blocker" for row in checked_in["obligations"]))
        self.assertEqual(86, len(checked_in["obligations"]))
        self.assertEqual("MISSING", checked_in["dependencies"][0]["state"])
        self.assertTrue(all(row["state"] == "OPEN" for row in checked_in["obligations"]))
        with self.assertRaisesRegex(gate.Invalid, "readiness remains NO"):
            validator.acceptance()

    def test_omitting_a_blocker_or_one_original_finding_is_rejected(self):
        for identity in ("B6-B59", "RD-MCP-011", "S4", "RD-B5-OBS-001"):
            with self.subTest(identity=identity):
                self.register = gate.build_template()
                self.register["obligations"] = [row for row in self.register["obligations"] if row["id"] != identity]
                with self.assertRaisesRegex(gate.Invalid, "obligation missing"):
                    self.validator().structure()

    def test_changing_operation_or_waiving_a_row_is_rejected(self):
        self.register["obligations"][0]["operation"] = "helpdesk_health.get"
        with self.assertRaisesRegex(gate.Invalid, "operation requirement changed"):
            self.validator().structure()
        self.register = gate.build_template()
        self.register["obligations"][0]["state"] = "UNSUPPORTED_BY_DESIGN"
        with self.assertRaisesRegex(gate.Invalid, "cannot waive"):
            self.validator().structure()

    def test_boolean_readiness_or_closed_label_does_not_create_acceptance(self):
        self.register["productionReadiness"] = "YES"
        with self.assertRaises(gate.Invalid):
            self.validator().structure()
        self.register["productionReadiness"] = "NO"
        self.register["obligations"][0]["state"] = "CLOSED"
        with self.assertRaises(gate.Invalid):
            self.validator().structure()

    def test_complete_synthetic_receipts_validate_without_any_network_or_mutation(self):
        self.make_ready()
        self.validator().structure()

    def test_native_hash_matches_generated_catalog_and_captured_system_text_json_encoding(self):
        export = gate.read_json(gate.ROOT / "docs/contracts/mcp-operation-catalog.json", native_numbers=True)
        self.assertEqual(export["catalogHash"], gate.native_json_hash(export["operations"]))
        self.assertEqual(export["schemaHash"], gate.native_json_hash(export["toolSchemas"]))
        # Captured with the pinned SDK's JsonObject.ToJsonString(), the same
        # serializer used by McpOperationCatalog and the native catalog export.
        value = {"ascii": '"\\\b\f\n\r\t\0<&>\'+`\x7f', "unicode": "München 中文 😀",
                 "literal": "\\u00af", "integer": 9223372036854775807}
        self.assertEqual("1e83631d7b197836599b6f4386517eaa4a76334f6b7258be00cf81b37b979ab5", gate.native_json_hash(value))
        # Parsed JsonNode numbers retain these exact spellings when serialized.
        numbers = "[1.0,1.000,1e-09,1e+09,1.23456789E+20,-0.0]"
        path = self.directory / "native-numbers.json"
        path.write_text(numbers)
        self.assertEqual(hashlib.sha256(numbers.encode()).hexdigest(), gate.native_json_hash(gate.read_json(path, native_numbers=True)))

    def test_unrelated_declared_catalog_or_schema_hash_is_rejected(self):
        for field, error in (("catalogHash", "actual exported operations"), ("schemaHash", "actual exported tool schemas")):
            with self.subTest(field=field):
                self.register = gate.build_template()
                self.make_ready(lambda export: export.update({field: hashlib.sha256(b"unrelated native hash").hexdigest()}))
                with self.assertRaisesRegex(gate.Invalid, error):
                    self.validator().structure()

    def test_mutated_catalog_or_schema_content_cannot_retain_its_old_semantic_hash(self):
        for field, error in (("operations", "actual exported operations"), ("toolSchemas", "actual exported tool schemas")):
            with self.subTest(field=field):
                self.register = gate.build_template()
                def change(export):
                    target = export[field][0] if field == "operations" else export[field][0]["inputSchema"]
                    target["description"] = "Changed content with an unchanged hash"
                self.make_ready(change)
                with self.assertRaisesRegex(gate.Invalid, error):
                    self.validator().structure()

    def test_declared_tool_and_operation_counts_must_match_actual_integer_populations(self):
        for field, value, error in (("toolCount", 1, "live tool count"), ("toolCount", "28", "live tool count"),
                                    ("operationCount", 154, "operation denominator"), ("operationCount", "155", "operation denominator")):
            with self.subTest(field=field, value=value):
                self.register = gate.build_template()
                self.make_ready(lambda export: export.update({field: value}))
                with self.assertRaisesRegex(gate.Invalid, error):
                    self.validator().structure()

    def test_duplicate_tool_or_operation_rows_cannot_hide_behind_unique_counts(self):
        for field, hash_field, error in (("operations", "catalogHash", "operation denominator"),
                                          ("toolSchemas", "schemaHash", "duplicate or inconsistent wire/export tool schemas")):
            with self.subTest(field=field):
                self.register = gate.build_template()
                def change(export):
                    export[field].append(copy.deepcopy(export[field][0]))
                    export[hash_field] = gate.native_json_hash(export[field])
                self.make_ready(change)
                with self.assertRaisesRegex(gate.Invalid, error):
                    self.validator().structure()

    def test_empty_extra_tool_cannot_inflate_the_live_tool_population(self):
        def change(export):
            export["toolSchemas"].append({"tool": "helpdesk_empty", "inputSchema": {"type": "object"}})
            export["schemaHash"] = gate.native_json_hash(export["toolSchemas"])
        self.make_ready(change)
        with self.assertRaisesRegex(gate.Invalid, "operation tool population disagree"):
            self.validator().structure()

    def test_restoration_and_final_review_cannot_precede_the_work_they_verify(self):
        for field, observed, error in (("restoration", "2026-10-18T00:10:00Z", "restoration verification predates"),
                                       ("independentReview", "2026-10-18T00:54:59Z", "review predates restoration"),
                                       ("closure", "2026-10-18T00:57:00Z", "review predates an individual")):
            with self.subTest(field=field):
                self.register = gate.build_template()
                self.make_ready()
                if field == "closure":
                    row = self.register["obligations"][0]
                    row["closure"] = self.change_receipt(row["closure"], closedAt=observed)
                else:
                    campaign = self.register["campaign"]
                    campaign[field] = self.change_receipt(campaign[field], verifiedAt=observed)
                with self.assertRaisesRegex(gate.Invalid, error):
                    self.validator().structure()

    def test_equal_completion_boundaries_and_correct_optional_tool_count_are_valid(self):
        self.make_ready(lambda export: export.update({"toolCount": len(export["toolSchemas"])}))
        campaign = self.register["campaign"]
        campaign["restoration"] = self.change_receipt(campaign["restoration"], verifiedAt="2026-10-18T00:40:00Z")
        campaign["independentReview"] = self.change_receipt(campaign["independentReview"], verifiedAt="2026-10-18T00:50:00Z")
        self.validator().structure()

    def test_prior_denial_preview_noop_and_historical_response_cannot_replace_positive(self):
        self.make_ready()
        row = self.register["obligations"][0]
        reference = row["caseEvidence"]["positive"]
        for observed in ("denied", "preview", "no_op", "historical"):
            with self.subTest(observed=observed):
                row["caseEvidence"]["positive"] = self.change_receipt(reference, executionKind=observed)
                with self.assertRaisesRegex(gate.Invalid, "cannot substitute"):
                    self.validator().structure()

    def test_same_owner_cannot_independently_close_own_work(self):
        self.make_ready()
        row = self.register["obligations"][0]
        row["owners"]["verifier"] = {"name": "Delivery Owner Alpha", "acknowledgedAt": "2026-10-18T00:00:00Z",
            "acknowledgement": self.acknowledge(row["id"], "verifier", "Delivery Owner Alpha")}
        with self.assertRaisesRegex(gate.Invalid, "not independent"):
            self.validator().structure()

    def test_stale_or_other_source_evidence_cannot_close_fresh_campaign(self):
        self.make_ready()
        row = self.register["obligations"][0]
        row["caseEvidence"]["positive"] = self.change_receipt(row["caseEvidence"]["positive"], startedAt="2026-10-17T00:00:00Z")
        with self.assertRaisesRegex(gate.Invalid, "outside the fresh campaign"):
            self.validator().structure()

    def test_source_image_and_native_manifest_mismatch_are_rejected(self):
        self.make_ready()
        campaign = self.register["campaign"]
        deployment = gate.read_json(self.directory / campaign["deployment"]["path"])
        deployment["components"]["mcp-http"]["sourceRevision"] = hashlib.sha1(b"wrong source").hexdigest()
        campaign["deployment"] = self.write(deployment, campaign["deployment"]["path"])
        with self.assertRaisesRegex(gate.Invalid, "deployed identity disagrees"):
            self.validator().structure()

    def test_skipping_a_historical_pass_in_the_full_catalog_is_rejected(self):
        self.make_ready()
        self.register["campaign"]["operations"] = [row for row in self.register["campaign"]["operations"] if row["id"] != "helpdesk_health.get"]
        with self.assertRaisesRegex(gate.Invalid, "entire historical/live operation denominator"):
            self.validator().structure()

    def test_lost_postcondition_is_rejected(self):
        self.make_ready()
        row = self.register["obligations"][0]
        row["caseEvidence"]["positive"] = self.change_receipt(row["caseEvidence"]["positive"], postconditionEvidence=None)
        with self.assertRaisesRegex(gate.Invalid, "missing evidence reference"):
            self.validator().structure()

    def test_original_case_corpus_cannot_be_replaced_by_an_aggregate_count(self):
        self.make_ready()
        self.register["campaign"]["historicalCases"] = []
        with self.assertRaisesRegex(gate.Invalid, "omits cases from the original immutable corpus"):
            self.validator().structure()
        self.register["dependencies"][0]["state"] = "MISSING"
        with self.assertRaisesRegex(gate.Invalid, "prerequisite has not been demonstrated"):
            self.validator().structure()

    def test_mutated_evidence_bytes_and_private_root_escape_are_rejected(self):
        reference = self.write({"observation": "before"})
        (self.directory / reference["path"]).write_text("changed")
        with self.assertRaisesRegex(gate.Invalid, "hash mismatch"):
            self.validator().evidence(reference, "test")
        reference["path"] = "../outside.json"
        with self.assertRaisesRegex(gate.Invalid, "escapes private root"):
            self.validator().evidence(reference, "test")

    def test_duplicate_json_properties_and_duplicate_rows_are_rejected(self):
        path = self.directory / "duplicate.json"
        path.write_text('{"productionReadiness":"NO","productionReadiness":"YES"}')
        with self.assertRaisesRegex(gate.Invalid, "duplicate JSON property"):
            gate.read_json(path)
        self.register["obligations"].append(copy.deepcopy(self.register["obligations"][0]))
        with self.assertRaisesRegex(gate.Invalid, "duplicate obligation"):
            self.validator().structure()

    def test_soak_short_duration_or_unhealthy_status_does_not_pass(self):
        self.make_ready()
        campaign = self.register["campaign"]
        campaign["soak"] = self.change_receipt(campaign["soak"], completedAt="2026-10-18T00:26:00Z")
        with self.assertRaisesRegex(gate.Invalid, "at least 15 minutes"):
            self.validator().structure()

    def test_latency_budget_cannot_be_retroactively_agreed_or_exceeded(self):
        self.make_ready()
        row = next(item for item in self.register["obligations"] if item["id"] == "RD-B5-OBS-001")
        reference = row["caseEvidence"]["profile_and_budget"]
        receipt = gate.read_json(self.directory / reference["path"])
        receipt["latencyBudget"]["agreedAt"] = "2026-10-18T00:30:00Z"
        row["caseEvidence"]["profile_and_budget"] = self.write(receipt, reference["path"])
        with self.assertRaisesRegex(gate.Invalid, "must be prospective"):
            self.validator().structure()
        receipt["latencyBudget"]["agreedAt"] = "2026-10-18T00:00:00Z"
        receipt["latencyBudget"]["measuredMs"] = 1501
        row["caseEvidence"]["profile_and_budget"] = self.write(receipt, reference["path"])
        with self.assertRaisesRegex(gate.Invalid, "exceeds or lacks"):
            self.validator().structure()

    def test_unobserved_mutation_is_not_a_successful_positive(self):
        self.make_ready()
        row = next(item for item in self.register["obligations"] if item["id"] == "B6-B09")
        row["caseEvidence"]["positive"] = self.change_receipt(row["caseEvidence"]["positive"], mutationObserved=False)
        with self.assertRaisesRegex(gate.Invalid, "mutation postcondition missing"):
            self.validator().structure()


if __name__ == "__main__":
    unittest.main(verbosity=2)

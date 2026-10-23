#!/usr/bin/env python3
"""Validate the beta.6 closure template or a private deployed acceptance receipt.

This is an offline evidence-completeness gate. It makes no requests, changes no
services, and cannot replace the independent review of the evidence's meaning.
"""

import argparse
import copy
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[2]
DEFAULT_REGISTER = ROOT / "docs/implementation/0.1.2-beta.6-acceptance/closure-register.json"
BASELINE_CATALOG = ROOT / "docs/contracts/mcp-operation-catalog-beta4.json"
VERSION = "0.1.2-beta.6"
IMAGE_FAMILIES = {
    "api": "ghcr.io/bostontechnologies/rateldesk-api",
    "web": "ghcr.io/bostontechnologies/rateldesk-web",
    "mcp-http": "ghcr.io/bostontechnologies/rateldesk-mcp-http",
}
ISSUES = "https://github.com/BostonTechnologies/RatelDesk/issues/"
ROLES = ("accountable", "implementation", "prerequisite", "verifier")
STATES = ("OPEN", "PREREQUISITE_DEMONSTRATED", "IMPLEMENTATION_VERIFIED", "ACCEPTANCE_PASS", "CLOSED")
SHA256 = re.compile(r"[0-9a-f]{64}\Z")
SOURCE_SHA = re.compile(r"[0-9a-f]{40}\Z")
UNSUPPORTED = {"helpdesk_config.set", "helpdesk_config.unset"}

# These are historical row identities, not a count derived from successful tests.
BLOCKED_KEYS = """
helpdesk_ai_assistant.report
helpdesk_connectivity.orchestration
helpdesk_connectivity.orchestration_jobs
helpdesk_connectivity.orchestration_tenants
helpdesk_connectivity.orchestration_request_definitions
helpdesk_connectivity.orchestration_bindings
helpdesk_connectivity.orchestration_test
helpdesk_incidents.peek
helpdesk_incidents.assign_self
helpdesk_search.users
helpdesk_search.customers
helpdesk_search.organizations
helpdesk_requests.assign_self
helpdesk_customers.list
helpdesk_customers.get
helpdesk_customers.auth_status
helpdesk_admin_mutations.create_organization
helpdesk_admin_mutations.update_organization
helpdesk_admin_mutations.delete_organization
helpdesk_admin_mutations.create_customer
helpdesk_admin_mutations.update_customer
helpdesk_admin_mutations.delete_customer
helpdesk_admin_mutations.create_user
helpdesk_admin_mutations.update_user
helpdesk_admin_mutations.delete_user
helpdesk_admin_mutations.create_category
helpdesk_admin_mutations.update_category
helpdesk_admin_mutations.delete_category
helpdesk_admin_mutations.create_service
helpdesk_admin_mutations.update_service
helpdesk_admin_mutations.delete_service
helpdesk_admin_mutations.create_request_form
helpdesk_admin_mutations.create_service_request_form
helpdesk_admin_mutations.provision_user
helpdesk_admin_mutations.update_organization_branding
helpdesk_admin_mutations.update_request_form
helpdesk_admin_mutations.delete_request_form
helpdesk_admin_mutations.customer_invite
helpdesk_admin_mutations.customer_resend_invite
helpdesk_admin_mutations.customer_disable_login
helpdesk_admin_mutations.customer_sync_authentik
helpdesk_request_tasks.retry
helpdesk_request_tasks.bulk_retry
helpdesk_self_service.requests
helpdesk_self_service.get_request
helpdesk_request_forms.by_service
helpdesk_changes.bulk_state
helpdesk_changes.assign
helpdesk_changes.assign_self
helpdesk_users.list
helpdesk_users.get
helpdesk_users.by_email
helpdesk_logs.search
helpdesk_organizations.list
helpdesk_organizations.get
helpdesk_organizations.tenants
helpdesk_organizations.tenant_lookup
helpdesk_organizations.change_participants
helpdesk_services.list
""".split()

DEPENDENCIES = {
    "original_case_corpus": "Original immutable case corpus, beta.5 reproduction evidence and historical results; absent from this public template.",
    "administrator_http_identity": "Supported bounded HTTP identity with actual required administrator authority and documented issuance, expiry and revocation.",
    "provider_configuration": "Actual enabled identity/provider mode and selected orchestration peer; no authentication or target fallback.",
    "ai_identity_and_invocation": "Exact AuthentikAiAgent scheme, issuer/audience/groups and a real invocation binding.",
    "eligible_support_caller": "Actual eligible caller, approved support membership and organization-restricted coverage.",
    "linked_customer_identity": "Authenticated enabled linked customer, owned requests and eligible form audience.",
    "automation_peer_tenant_binding": "Failed automated tasks plus peer, organization tenant linkage, binding and callback identity.",
    "isolated_mail_capture": "Isolated sender and independent capture receipts; preserve paused worker, disabled saved sender and unrelated queues.",
    "distinct_actors_and_approver": "Distinct notification actors and actual designated change approver.",
    "historical_timeline_snapshots": "Signed public history and controlled legacy transactional snapshots with exact provenance.",
    "credential_boundary_fixtures": "Scoped, foreign-organization, expired, separately disposable revoked and fresh status-only credentials.",
    "prospective_latency_budget": "Prospectively agreed notification-summary latency workload, metric and budget.",
}

FAMILIES = [
    "Reject undefined incident states atomically",
    "Preserve flat task lifecycle responses and uncertain-outcome reconciliation",
    "Preserve query aliases, filtered counts and paging in all four ticket lists",
    "Preserve named task priorities and atomic invalid-edit rejection",
    "Preserve governed change state, approval, edit-lock and lifecycle routes",
    "Preserve customer authentication routes, provider authority and delivery evidence",
    "Preserve executable schemas, previews and autonomous AI report binding",
    "Reject unsupported ticket edit fields atomically",
    "Preserve four fixed raw reads without arbitrary raw access",
    "Preserve mail dialog cancellation, reasons, evidence and queue behavior",
    "Preserve authority, transport, identifier and prerequisite contracts",
]

BETA4_REGRESSIONS = (
    "helpdesk_notifications.summary", "helpdesk_incidents.list", "helpdesk_requests.list",
    "helpdesk_changes.list", "helpdesk_request_tasks.list", "helpdesk_incidents.timeline",
    "helpdesk_requests.timeline", "helpdesk_changes.timeline",
)

SCENARIOS = {
    "S1": ("Captured mail lifecycle", ["isolated_mail_capture"], {
        "invitation": "positive", "submission": "positive", "reply": "positive", "forward": "positive",
        "retry": "positive", "cancellation": "positive", "partial_outcome": "positive", "uncertain_outcome": "positive",
    }),
    "S2": ("Notification actor isolation and actual change approval", ["distinct_actors_and_approver"], {
        "complete_authorized_populations": "positive", "distinct_actors": "positive",
        "cross_actor_isolation": "expected_negative", "designated_approver_branches": "positive",
    }),
    "S3": ("Genuine orchestration and AI lifecycle", ["automation_peer_tenant_binding", "ai_identity_and_invocation"], {
        "dispatch_and_callback": "positive", "bound_ai_invocation": "positive", "duplicate": "positive",
        "out_of_order": "positive", "wrong_binding": "expected_negative", "timeout_recovery": "positive",
        "parent_workflow_effects": "positive", "cross_invocation_event_collision": "expected_negative",
        "terminal_state_regression": "expected_negative",
    }),
    "S4": ("Exact historical timeline recovery", ["historical_timeline_snapshots"], {
        "signed_public_history": "positive", "legacy_exact_recovery": "positive",
        "legacy_unavailable_is_explicit": "positive", "ambiguous_provenance_rejected": "expected_negative",
    }),
    "S5": ("Credential boundaries, reload, resumability and independent checks", ["credential_boundary_fixtures"], {
        "scoped_positive": "positive", "foreign_organization": "expected_negative", "expired": "expected_negative",
        "separately_disposable_revoked": "expected_negative", "fresh_status_only": "positive",
        "fresh_process_profile_reload": "positive", "resumability": "positive",
        "independent_api_cli_ui_storage": "positive",
    }),
}

SOURCE_OBSERVATIONS = [
    {"id": "B6-SRC-AI-01", "classification": "source_review", "historicalReproducedFailure": False,
     "title": "Bind duplicate callback event handling to the actual invocation",
     "issue": ISSUES + "234", "acceptanceObligations": ["B6-B01", "S3"]},
    {"id": "B6-SRC-AI-02", "classification": "source_review", "historicalReproducedFailure": False,
     "title": "Prevent an out-of-order callback from regressing a terminal invocation",
     "issue": ISSUES + "234", "acceptanceObligations": ["B6-B01", "S3"]},
]


class Invalid(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise Invalid(message)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"duplicate JSON property: {key}")
        result[key] = value
    return result


class NativeJsonNumber(str):
    """Keep parsed number spelling, as System.Text.Json's JsonNode does."""


def read_json(path, native_numbers=False):
    require(path.stat().st_size <= 8 * 1024 * 1024, "JSON receipt exceeds 8 MiB")
    options = {"parse_int": NativeJsonNumber, "parse_float": NativeJsonNumber} if native_numbers else {}
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object, **options)


def native_json_hash(value):
    """Hash ordered compact JsonNode JSON with its default JavaScriptEncoder.

    Native exports preserve property/array order and parsed number spelling.
    String escaping differs from Python JSON: HTML-sensitive ASCII is escaped,
    Unicode hexadecimal digits are uppercase, and embedded quotes use U+0022.
    """
    def compact(item):
        if isinstance(item, NativeJsonNumber):
            return str(item)
        if isinstance(item, dict):
            return "{" + ",".join(compact(key) + ":" + compact(value) for key, value in item.items()) + "}"
        if isinstance(item, list):
            return "[" + ",".join(compact(value) for value in item) + "]"
        encoded = json.dumps(item, ensure_ascii=True, separators=(",", ":"), allow_nan=False)

        def escape(match):
            token = match.group(0)
            if token == '\\"':
                return "\\u0022"
            if token.startswith("\\u"):
                return "\\u" + token[2:].upper()
            return "\\u" + format(ord(token), "04X") if len(token) == 1 else token

        # Consume complete escapes so a literal backslash-u sequence is retained.
        return re.sub(r'''\\(?:["\\bfnrt]|u[0-9a-f]{4})|[&'<>+`\x7f]''', escape, encoded)

    return hashlib.sha256(compact(value).encode("utf-8")).hexdigest()


def named(value):
    return isinstance(value, str) and len(value.strip()) >= 3 and not re.search(
        r"\b(pending|unknown|unassigned|placeholder|tbd|todo|synthetic|example|release owner|maintainer|administrator|executor)\b",
        value, re.IGNORECASE)


def timestamp(value, label):
    require(isinstance(value, str), f"{label}: missing timestamp")
    try:
        result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise Invalid(f"{label}: invalid timestamp") from exc
    require(result.tzinfo is not None, f"{label}: timezone is required")
    return result


def digest(value, label):
    require(isinstance(value, str) and SHA256.fullmatch(value), f"{label}: expected SHA256")
    require(len(set(value)) > 1, f"{label}: placeholder digest")


def schema_operations(schema):
    """Read operation discriminators, without treating request enums as operations."""
    result = set()
    if not isinstance(schema, dict):
        return result
    selector = schema.get("properties", {}).get("operation", {})
    if isinstance(selector.get("const"), str):
        result.add(selector["const"])
    if isinstance(selector.get("enum"), list):
        result.update(value for value in selector["enum"] if isinstance(value, str))
    for keyword in ("oneOf", "anyOf", "allOf"):
        for branch in schema.get(keyword, []):
            result.update(schema_operations(branch))
    return result


def specs():
    baseline = read_json(BASELINE_CATALOG)
    definitions = {f"{r['tool']}.{r['operation']}": r for r in baseline["operations"]}
    result = {}
    for number, operation in enumerate(BLOCKED_KEYS, 1):
        identity = f"B6-B{number:02d}"
        if number in (1, 53):
            group, dependencies = "exact_ai_identity", ["ai_identity_and_invocation", "provider_configuration"]
        elif number in (9, 13):
            group, dependencies = "eligible_self_assignment", ["eligible_support_caller"]
        elif number in (42, 43):
            group, dependencies = "eligible_automation_retry", ["automation_peer_tenant_binding", "provider_configuration"]
        elif number in (44, 45):
            group, dependencies = "linked_customer_identity", ["linked_customer_identity"]
        else:
            group, dependencies = "instance_global_authority", ["administrator_http_identity", "provider_configuration"]
        if number == 34:
            dependencies.append("linked_customer_identity")
        if number in (38, 39):
            dependencies.append("isolated_mail_capture")
        if 2 <= number <= 7:
            dependencies.append("automation_peer_tenant_binding")
        definition = definitions[operation]
        cases = {"positive": "positive", "isolation": "expected_negative"}
        if definition["previewSupported"]:
            cases["preview"] = "preview"
        result[identity] = {
            "kind": "historical_blocker", "operation": operation, "title": operation,
            "group": group, "dependencies": dependencies, "cases": cases, "issues": [ISSUES + "219"],
        }
    result["RD-B5-MCP-001"] = {
        "kind": "release_defect", "operation": None, "title": "Durable worklog queue acceptance must not claim email delivery",
        "group": "mail_truthfulness", "dependencies": ["isolated_mail_capture", "original_case_corpus"],
        "issues": [ISSUES + "219"], "cases": {
            "manual_failure_missing_sender": "positive", "manual_failure_disabled_sender": "positive",
            "paused_worker": "positive", "internal_note_no_mail": "positive",
            "public_note_closure_workflow": "positive", "captured_success": "positive", "captured_failure": "positive",
            "captured_partial": "positive", "captured_uncertain": "positive", "duplicate": "positive",
            "cancellation": "positive", "retry": "positive", "exact_event_effect_binding": "positive",
            "sqlite_regression": "implementation", "postgresql_regression": "implementation",
        },
    }
    for identity, (title, dependencies, cases) in SCENARIOS.items():
        result[identity] = {"kind": "mandatory_scenario", "operation": None, "title": title,
                            "group": identity, "dependencies": list(dependencies), "cases": dict(cases), "issues": [ISSUES + "219"]}
    for number, title in enumerate(FAMILIES, 1):
        result[f"RD-MCP-{number:03d}"] = {
            "kind": "historical_finding", "operation": None, "title": title, "group": "original_eleven_families",
            "dependencies": ["original_case_corpus"], "cases": {"fresh_family_regression": "positive", "isolation": "expected_negative"},
            "issues": [ISSUES + str(number + 206), ISSUES + "219"],
        }
    for number, operation in enumerate(BETA4_REGRESSIONS, 1):
        result[f"B6-R{number:02d}"] = {
            "kind": "beta4_failed_operation", "operation": operation, "title": "Rerun repaired " + operation,
            "group": "beta4_failures_first", "dependencies": ["original_case_corpus"],
            "cases": {"positive": "positive", "isolation": "expected_negative"},
            "issues": [ISSUES + str(222 if number == 1 else 223 if number <= 5 else 224), ISSUES + "219"],
        }
    result["RD-B5-OBS-001"] = {
        "kind": "performance_observation", "operation": "helpdesk_notifications.summary",
        "title": "Profile summary latency against a prospectively agreed budget while preserving correct counts",
        "group": "prospective_performance", "dependencies": ["prospective_latency_budget"],
        "cases": {"profile_and_budget": "positive", "count_correctness": "positive"}, "issues": [ISSUES + "219"],
    }
    result["B6-N01"] = {
        "kind": "new_operation", "operation": "helpdesk_auth.access", "title": "Verify the actual configured HTTP execution identity and access",
        "group": "beta6_catalog_addition", "dependencies": ["credential_boundary_fixtures", "provider_configuration"],
        "cases": {"positive": "positive", "isolation": "expected_negative", "setup_verification_denied": "expected_negative",
            "scoped_admin_owner_stays_scoped": "positive", "foreign_resource": "expected_negative",
            "expired": "expected_negative", "revoked": "expected_negative", "disabled_account": "expected_negative",
            "external_shared_principal_binding": "positive", "external_admin_without_ai_scheme": "positive",
            "discovery_metadata_is_not_verified_authority": "positive"},
        "issues": [ISSUES + "217", ISSUES + "219"],
    }
    delivery_issues = {"instance_global_authority": [228], "exact_ai_identity": [228, 234],
        "eligible_self_assignment": [229], "eligible_automation_retry": [231], "linked_customer_identity": [230],
        "mail_truthfulness": [227], "S1": [227, 232], "S2": [211, 222, 232], "S3": [231, 234],
        "S4": [224, 232], "S5": [228, 232], "prospective_performance": [233], "beta6_catalog_addition": [228]}
    for spec in result.values():
        spec["issues"] = list(dict.fromkeys(spec["issues"] + [ISSUES + "226"]
                              + [ISSUES + str(issue) for issue in delivery_issues.get(spec["group"], [])]))
    return result


def build_template():
    obligations = []
    for identity, spec in specs().items():
        obligations.append({"id": identity, **spec, "state": "OPEN", "workStartedAt": None,
            "owners": {role: {"name": None, "acknowledgedAt": None, "acknowledgement": None} for role in ROLES},
            "implementationEvidence": [], "caseEvidence": {case: None for case in spec["cases"]}, "closure": None})
    return {
        "schemaVersion": 1, "release": VERSION, "productionReadiness": "NO", "acceptanceIssue": ISSUES + "219",
        "sourceObservations": copy.deepcopy(SOURCE_OBSERVATIONS),
        "historicalBaseline": {"version": "0.1.2-beta.5", "sourceRevision": "07ec0a48f4b24656f56eac265e38ac24c687ffa4",
            "toolCount": 28, "operationCount": 154, "passedOperationsToRerun": 91, "failedOperationsToRerun": 2,
            "blockedOperationsToExecute": 59, "intentionalHttpRestrictions": sorted(UNSUPPORTED)},
        "dependencies": [{"id": identity, "requirement": text, "state": "MISSING" if identity == "original_case_corpus" else "PENDING",
                          "owner": None, "acknowledgement": None, "demonstratedAt": None, "evidence": None}
                         for identity, text in DEPENDENCIES.items()],
        "obligations": obligations,
        "campaign": {"id": None, "startedAt": None, "completedAt": None, "sourceRevision": None,
            "releaseManifest": None, "deployment": None, "catalog": None, "operations": [], "historicalCases": [],
            "soak": None, "restoration": None, "independentReview": None},
    }


class Validator:
    def __init__(self, register, evidence_root=None, now=None):
        self.register = register
        self.evidence_root = evidence_root.resolve() if evidence_root else None
        self.now = now or datetime.now(timezone.utc)
        self.expected = specs()
        self.context = None
        self.dependency_rows = {}
        self.obligation_rows = {}
        self.checked_references = 0

    def evidence(self, reference, label, json_document=False, native_numbers=False):
        require(self.evidence_root is not None, f"{label}: --evidence-root is required")
        require(isinstance(reference, dict) and set(reference) == {"path", "sha256"}, f"{label}: missing evidence reference")
        digest(reference["sha256"], label)
        relative = reference["path"]
        require(isinstance(relative, str) and relative and not Path(relative).is_absolute(), f"{label}: use an evidence-root-relative path")
        path = (self.evidence_root / relative).resolve()
        require(path.is_relative_to(self.evidence_root), f"{label}: evidence escapes private root")
        require(path.is_file(), f"{label}: evidence file is absent")
        require(path.stat().st_size <= 64 * 1024 * 1024, f"{label}: evidence exceeds 64 MiB")
        require(hashlib.sha256(path.read_bytes()).hexdigest() == reference["sha256"], f"{label}: evidence hash mismatch")
        self.checked_references += 1
        return read_json(path, native_numbers=native_numbers) if json_document else reference["sha256"]

    def structure(self):
        data = self.register
        require(data.get("schemaVersion") == 1 and data.get("release") == VERSION, "wrong schema/release")
        require(data.get("productionReadiness") in ("YES", "NO"), "productionReadiness must be YES or NO")
        require(data.get("acceptanceIssue") == ISSUES + "219", "#219 acceptance obligation cannot be removed")
        require(data.get("historicalBaseline") == build_template()["historicalBaseline"], "historical baseline/denominator changed")
        require(data.get("sourceObservations") == SOURCE_OBSERVATIONS, "source observations must remain separate from historical reproduced failures")
        dependencies = data.get("dependencies")
        require(isinstance(dependencies, list), "dependencies must be a list")
        self.dependency_rows = self.index(dependencies, "dependency")
        require(set(self.dependency_rows) == set(DEPENDENCIES), "required dependency omitted or duplicated")
        for identity, row in self.dependency_rows.items():
            require(row.get("state") in ("MISSING", "PENDING", "DEMONSTRATED"), f"{identity}: invalid dependency state")
            require(row.get("requirement") == DEPENDENCIES[identity], f"{identity}: dependency requirement changed")
        self.obligation_rows = self.index(data.get("obligations"), "obligation")
        require(set(self.obligation_rows) == set(self.expected), "obligation missing, added without contract update, or duplicated")
        for identity, spec in self.expected.items():
            row = self.obligation_rows[identity]
            for key in ("kind", "operation", "group", "dependencies", "cases"):
                require(row.get(key) == spec[key], f"{identity}: {key} requirement changed")
            require(set(spec["issues"]).issubset(row.get("issues", [])), f"{identity}: historical issue link missing")
            require(row.get("state") in STATES, f"{identity}: state cannot waive or relabel acceptance")
            require(isinstance(row.get("owners"), dict) and set(row["owners"]) == set(ROLES), f"{identity}: owner role omitted")
            for role in ROLES:
                require(isinstance(row["owners"][role], dict) and set(row["owners"][role]) == {"name", "acknowledgedAt", "acknowledgement"},
                        f"{identity}: malformed {role} owner")
            require(isinstance(row.get("caseEvidence"), dict) and set(row["caseEvidence"]) == set(spec["cases"]),
                    f"{identity}: individual case evidence slot omitted")
            require(isinstance(row.get("implementationEvidence"), list), f"{identity}: missing implementation evidence list")
        require(isinstance(data.get("campaign"), dict), "campaign must be an object")
        require(isinstance(data["campaign"].get("operations"), list), "campaign.operations must be a list")
        require(isinstance(data["campaign"].get("historicalCases"), list), "campaign.historicalCases must be a list")
        # Structure mode must never approve a fabricated completed register.
        if data["productionReadiness"] == "YES":
            self.acceptance()
        else:
            for row in self.dependency_rows.values():
                if row["state"] == "DEMONSTRATED":
                    self.dependency(row["id"])
            for identity, row in self.obligation_rows.items():
                if row["state"] != "OPEN":
                    self.obligation(identity)

    @staticmethod
    def index(rows, label):
        require(isinstance(rows, list), f"{label} rows must be a list")
        result = {}
        for row in rows:
            require(isinstance(row, dict) and isinstance(row.get("id"), str), f"invalid {label} row")
            require(row["id"] not in result, f"duplicate {label}: {row['id']}")
            result[row["id"]] = row
        return result

    def campaign_time(self, value, label):
        campaign = self.register["campaign"]
        start = timestamp(campaign.get("startedAt"), "campaign start")
        end = timestamp(campaign.get("completedAt"), "campaign end")
        require(start <= end <= self.now, "campaign timestamps must be ordered and not in the future")
        observed = timestamp(value, label)
        require(start <= observed <= end, f"{label}: evidence is outside the fresh campaign")
        return observed

    def acknowledgement(self, reference, name, role, identity, before=None):
        require(named(name), f"{identity}: missing named {role} owner")
        receipt = self.evidence(reference, f"{identity} {role} acknowledgement", True)
        require(receipt.get("name") == name and receipt.get("role") == role and receipt.get("obligationId") == identity,
                f"{identity}: acknowledgement identity/role mismatch")
        require(receipt.get("acknowledged") is True, f"{identity}: acknowledgement is not affirmative")
        acknowledged = timestamp(receipt.get("acknowledgedAt"), f"{identity} acknowledgement")
        target = timestamp(receipt.get("targetDeliveryAt"), f"{identity} target delivery")
        require(acknowledged <= target and acknowledged <= self.now, f"{identity}: invalid acknowledgement/target date")
        if before:
            require(acknowledged <= before, f"{identity}: owner acknowledged after work began")
        return acknowledged

    def dependency(self, identity):
        row = self.dependency_rows[identity]
        require(row["state"] == "DEMONSTRATED", f"{identity}: prerequisite has not been demonstrated")
        before = timestamp(self.register["campaign"].get("startedAt"), "campaign start")
        self.acknowledgement(row.get("acknowledgement"), row.get("owner"), "prerequisite", identity, before)
        receipt = self.evidence(row.get("evidence"), f"{identity} prerequisite evidence", True)
        observed = timestamp(row.get("demonstratedAt"), f"{identity} demonstrated time")
        require(observed <= before, f"{identity}: dependency was not demonstrated before acceptance")
        require(receipt.get("dependencyId") == identity and receipt.get("demonstratedAt") == row["demonstratedAt"],
                f"{identity}: prerequisite receipt mismatch")
        require(receipt.get("demonstrated") is True and receipt.get("deployedTargetVerified") is True,
                f"{identity}: actual prerequisite/target evidence is absent")
        campaign = self.register["campaign"]
        require(receipt.get("campaignId") == campaign["id"] and receipt.get("sourceRevision") == campaign["sourceRevision"],
                f"{identity}: prerequisite belongs to another campaign/source")
        self.evidence(receipt.get("observation"), f"{identity} independent prerequisite observation")
        if identity == "original_case_corpus":
            sources = receipt.get("immutableSources")
            require(isinstance(sources, list) and sources, "original_case_corpus: immutable historical sources are absent")
            for source in sources:
                self.evidence(source, "original immutable corpus source")
            manifest = self.evidence(receipt.get("caseManifest"), "original case manifest", True)
            self.historical_cases = self.index(manifest.get("cases"), "original case")
            require(self.historical_cases, "original case corpus has no cases")
            for case_id, case in self.historical_cases.items():
                require(case.get("executionKind") in ("positive", "expected_negative", "preview", "unsupported"),
                        f"{case_id}: original case disposition cannot waive a requirement")
                if case["executionKind"] == "unsupported":
                    require(case.get("operation") in UNSUPPORTED, f"{case_id}: unsupported historical case is not an intentional restriction")

    def deployment(self):
        if self.context is not None:
            return self.context
        campaign = self.register["campaign"]
        require(isinstance(campaign.get("id"), str) and campaign["id"].strip(), "missing fresh campaign identity")
        source = campaign.get("sourceRevision")
        require(isinstance(source, str) and SOURCE_SHA.fullmatch(source) and len(set(source)) > 1, "missing exact source revision")
        manifest = self.evidence(campaign.get("releaseManifest"), "release manifest", True)
        require(manifest.get("version") == VERSION and manifest.get("sourceRevision") == source, "release manifest version/source mismatch")
        require(manifest.get("tag") in ("v" + VERSION, "RatelDesk-" + VERSION), "release manifest tag mismatch")
        deployment = self.evidence(campaign.get("deployment"), "deployed identity", True)
        self.campaign_time(deployment.get("observedAt"), "deployment observation")
        require(deployment.get("campaignId") == campaign["id"], "deployment belongs to another campaign")
        containers = {item.get("image"): item.get("digest") for item in manifest.get("containers", [])}
        require(len(containers) == 3, "manifest must retain three image families")
        observed_components = deployment.get("components", {})
        for component, image_family in IMAGE_FAMILIES.items():
            image_name = f"{image_family}:{VERSION}"
            expected_digest = containers.get(image_name, "")
            require(expected_digest.startswith("sha256:"), f"{component}: immutable image digest missing")
            digest(expected_digest.removeprefix("sha256:"), f"{component} image")
            observed = observed_components.get(component, {})
            require(observed.get("version") == VERSION and observed.get("sourceRevision") == source
                    and observed.get("digest") == expected_digest, f"{component}: deployed identity disagrees with release")
            self.evidence(observed.get("observation"), f"{component} deployed observation")
        assets = manifest.get("assets", {})
        native_tools = deployment.get("nativeTools", {})
        for tool in ("cli", "mcp-stdio"):
            for rid in ("linux-x64", "linux-arm64", "win-x64"):
                name = f"rateldesk-{tool}-{VERSION}-{rid}.{'zip' if rid == 'win-x64' else 'tar.gz'}"
                digest(assets.get(name), f"{name} manifest checksum")
                observed = native_tools.get(name, {})
                require(observed.get("sha256") == assets[name] and observed.get("version") == VERSION
                        and observed.get("sourceRevision") == source, f"{name}: native identity/checksum disagreement")
                self.evidence(observed.get("observation"), f"{name} native verification")
        catalog = self.evidence(campaign.get("catalog"), "catalog receipt", True)
        self.campaign_time(catalog.get("observedAt"), "catalog observation")
        require(catalog.get("campaignId") == campaign["id"], "catalog belongs to another campaign")
        wire = self.evidence(catalog.get("wireDiscovery"), "wire discovery", True)
        export = self.evidence(catalog.get("exportedCatalog"), "exported catalog", True)
        wire_tools = wire.get("result", wire).get("tools")
        require(isinstance(wire_tools, list) and wire_tools, "wire tools/list discovery is missing")
        wire_schemas = {item["name"]: item["inputSchema"] for item in wire_tools}
        exported_tools = export.get("toolSchemas")
        require(isinstance(exported_tools, list) and exported_tools, "exported tool schemas are missing")
        export_schemas = {item["tool"]: item["inputSchema"] for item in exported_tools}
        require(len(wire_schemas) == len(wire_tools) and len(export_schemas) == len(exported_tools)
                and wire_schemas == export_schemas, "duplicate or inconsistent wire/export tool schemas")
        if "toolCount" in export:
            require(type(export["toolCount"]) is int and export["toolCount"] == len(export_schemas),
                    "declared live tool count disagrees with actual tool schemas")
        digest(catalog.get("semanticCatalogHash"), "semantic catalog hash")
        digest(catalog.get("schemaHash"), "aggregate input-schema hash")
        require(catalog["semanticCatalogHash"] == export.get("catalogHash") and catalog["schemaHash"] == export.get("schemaHash"),
                "semantic catalog/schema hashes disagree with export")
        native_export = self.evidence(catalog["exportedCatalog"], "exported native catalog hash", True, native_numbers=True)
        require(native_json_hash(native_export.get("operations")) == export["catalogHash"],
                "semantic catalog hash disagrees with actual exported operations")
        require(native_json_hash(native_export.get("toolSchemas")) == export["schemaHash"],
                "aggregate schema hash disagrees with actual exported tool schemas")
        self.evidence(catalog.get("semanticHashObservation"), "independent live semantic/schema hash observation")
        exported_operations = export.get("operations")
        require(isinstance(exported_operations, list) and exported_operations, "exported operations are missing")
        current = {f"{row['tool']}.{row['operation']}": row for row in exported_operations}
        require(type(export.get("operationCount")) is int and len(current) == len(exported_operations) == export["operationCount"],
                "duplicate or inconsistent live operation denominator")
        require({row["tool"] for row in exported_operations} == set(export_schemas),
                "live tool schemas and operation tool population disagree")
        wire_keys = {f"{tool}.{operation}" for tool, schema in wire_schemas.items() for operation in schema_operations(schema)}
        require(wire_keys == set(current), "wire discovery/export operation denominator disagreement")
        baseline = {f"{row['tool']}.{row['operation']}": row for row in read_json(BASELINE_CATALOG)["operations"]}
        require(set(baseline) <= set(current), "historical operations were removed from the acceptance denominator")
        require(set(catalog.get("addedOperations", [])) == set(current) - set(baseline), "catalog additions were not explicitly mapped")
        require(catalog.get("removedOperations") == [], "historical removals cannot satisfy this acceptance contract")
        changes = {key: "added" if key not in baseline else "unchanged" if row == baseline[key] else "changed" for key, row in current.items()}
        require(catalog.get("operationChanges") == changes, "every historical/live operation change must be explicitly mapped")
        self.context = {"campaignId": campaign["id"], "version": VERSION, "sourceRevision": source,
                        "deploymentSha256": campaign["deployment"]["sha256"], "catalogReceiptSha256": campaign["catalog"]["sha256"]}
        self.current_operations = current
        return self.context

    def case(self, reference, identity, case_id, kind, operation=None, verifier=None):
        receipt = self.evidence(reference, f"{identity}/{case_id}", True)
        require(receipt.get("obligationId") == identity and receipt.get("caseId") == case_id, f"{identity}/{case_id}: receipt binding mismatch")
        require(receipt.get("outcome") == "PASS" and receipt.get("executionKind") == kind,
                f"{identity}/{case_id}: denial, no-op, preview or historical result cannot substitute for the required execution")
        require(named(receipt.get("executor")) and named(receipt.get("verifier")), f"{identity}/{case_id}: named executor/verifier missing")
        if verifier:
            require(receipt["verifier"] == verifier, f"{identity}/{case_id}: verifier is not the assigned verifier")
        if kind == "implementation":
            require(receipt.get("sourceRevision") == self.register["campaign"].get("sourceRevision"), f"{identity}/{case_id}: implementation source mismatch")
            require(type(receipt.get("passedTests")) is int and receipt["passedTests"] > 0
                    and receipt.get("failedTests") == 0 and receipt.get("skippedTests") == 0, f"{identity}/{case_id}: incomplete regression execution")
            self.evidence(receipt.get("testResults"), f"{identity}/{case_id} test results")
            return receipt
        require(receipt.get("context") == self.deployment(), f"{identity}/{case_id}: campaign/source/deployment/catalog mismatch")
        require(receipt.get("origin") == "fresh_deployed" and receipt.get("transport") == "http",
                f"{identity}/{case_id}: fresh deployed HTTP execution is required")
        started = self.campaign_time(receipt.get("startedAt"), f"{identity}/{case_id} start")
        ended = self.campaign_time(receipt.get("completedAt"), f"{identity}/{case_id} end")
        require(started <= ended, f"{identity}/{case_id}: invalid execution order")
        if operation:
            require(receipt.get("operation") == operation, f"{identity}/{case_id}: wrong operation")
            require(operation in self.current_operations, f"{identity}/{case_id}: required operation is absent from live discovery")
            definition = self.current_operations[operation]
            if kind == "positive" and definition.get("confirmationRequired"):
                require(receipt.get("confirmedExecution") is True, f"{identity}/{case_id}: mutation was not confirmed")
            if kind == "positive" and not definition.get("readOnly"):
                require(receipt.get("mutationObserved") is True, f"{identity}/{case_id}: mutation postcondition missing")
        if kind == "positive":
            require(receipt.get("supportedPositive") is True, f"{identity}/{case_id}: supported positive execution missing")
        if kind == "preview":
            require(receipt.get("previewImmutable") is True, f"{identity}/{case_id}: preview changed state")
        require(receipt.get("postconditionSurface") in ("API", "CLI", "UI", "storage", "provider", "transport_capture"),
                f"{identity}/{case_id}: independent postcondition surface missing")
        for field in ("identityEvidence", "wireEvidence", "postconditionEvidence", "restorationEvidence"):
            self.evidence(receipt.get(field), f"{identity}/{case_id} {field}")
        if identity == "S1" or (identity == "RD-B5-MCP-001" and case_id.startswith("captured_")) or (
                identity in ("B6-B38", "B6-B39") and kind == "positive"):
            self.evidence(receipt.get("transportReceiptEvidence"), f"{identity}/{case_id} independent captured transport receipt")
        if identity == "RD-B5-OBS-001" and case_id == "profile_and_budget":
            budget = receipt.get("latencyBudget", {})
            require(budget.get("metric") in ("p50", "p95", "p99", "maximum"), "latency observation: explicit metric missing")
            require(type(budget.get("budgetMs")) in (int, float) and type(budget.get("measuredMs")) in (int, float)
                    and 0 <= budget["measuredMs"] <= budget["budgetMs"] < float("inf") and budget["budgetMs"] > 0,
                    "latency observation: measured result exceeds or lacks the agreed budget")
            require(timestamp(budget.get("agreedAt"), "latency budget agreement") <= started,
                    "latency budget must be prospective, not a retrospective SLA")
            self.evidence(budget.get("agreementEvidence"), "latency budget agreement")
            self.evidence(budget.get("workloadEvidence"), "latency budget workload/profile")
        return receipt

    def obligation(self, identity):
        row, spec = self.obligation_rows[identity], self.expected[identity]
        stage = STATES.index(row["state"])
        require(stage > 0, f"{identity}: OPEN")
        work_started = timestamp(row.get("workStartedAt"), f"{identity} work start")
        require(work_started <= self.now, f"{identity}: work start is in the future")
        before = min(timestamp(self.register["campaign"].get("startedAt"), "campaign start"), work_started)
        for role, owner in row["owners"].items():
            observed = self.acknowledgement(owner["acknowledgement"], owner["name"], role, identity, before)
            require(observed == timestamp(owner["acknowledgedAt"], f"{identity} {role} acknowledgement"), f"{identity}: owner acknowledgement time mismatch")
        verifier = row["owners"]["verifier"]["name"]
        require(verifier not in [row["owners"][role]["name"] for role in ROLES if role != "verifier"], f"{identity}: verifier is not independent of delivery owners")
        for dependency in spec["dependencies"]:
            self.dependency(dependency)
        if stage >= 2:
            require(row["implementationEvidence"], f"{identity}: implementation verification evidence missing")
            for evidence in row["implementationEvidence"]:
                self.evidence(evidence, f"{identity} implementation verification")
        if stage >= 3:
            for case_id, kind in spec["cases"].items():
                self.case(row["caseEvidence"][case_id], identity, case_id, kind, spec["operation"], verifier)
        if stage >= 4:
            closure = self.evidence(row.get("closure"), f"{identity} independent closure", True)
            require(closure.get("obligationId") == identity and closure.get("verifier") == verifier and closure.get("independentlyClosed") is True,
                    f"{identity}: independent closure receipt mismatch")
            require(closure.get("context") == self.deployment(), f"{identity}: closure identity mismatch")
            closed_at = self.campaign_time(closure.get("closedAt"), f"{identity} closure")
            for case_id, reference in row["caseEvidence"].items():
                receipt = self.evidence(reference, f"{identity}/{case_id} closure ordering", True)
                if self.expected[identity]["cases"][case_id] != "implementation":
                    require(timestamp(receipt["completedAt"], case_id) <= closed_at, f"{identity}: closure predates verification")

    def acceptance(self):
        require(self.register["productionReadiness"] == "YES", "production readiness remains NO")
        self.deployment()
        for identity in self.dependency_rows:
            self.dependency(identity)
        for identity, row in self.obligation_rows.items():
            require(row["state"] == "CLOSED", f"{identity}: obligation remains {row['state']}")
            self.obligation(identity)
        results = self.index(self.register["campaign"]["operations"], "campaign operation")
        require(set(results) == set(self.current_operations), "fresh campaign does not cover the entire historical/live operation denominator")
        for operation, row in results.items():
            if operation in UNSUPPORTED:
                require(row.get("state") == "UNSUPPORTED_BY_DESIGN" and row.get("reason") == "HTTP remote configuration persistence is disabled",
                        f"{operation}: intentional restriction must remain separately documented")
            else:
                require(row.get("state") == "PASS", f"{operation}: fresh supported positive is absent")
                self.case(row.get("evidence"), "catalog", operation, "positive", operation)
        historical_results = self.index(self.register["campaign"]["historicalCases"], "fresh original case")
        require(set(historical_results) == set(self.historical_cases), "fresh campaign omits cases from the original immutable corpus")
        for case_id, specification in self.historical_cases.items():
            row = historical_results[case_id]
            if specification["executionKind"] == "unsupported":
                require(row.get("state") == "UNSUPPORTED_BY_DESIGN", f"{case_id}: intentional historical restriction must remain explicit")
            else:
                require(row.get("state") == "PASS", f"{case_id}: original case has no fresh result")
                self.case(row.get("evidence"), "historical-corpus", case_id, specification["executionKind"], specification.get("operation"))
        # These eight deployed regressions precede the rest of the full catalog.
        regression_end = max(timestamp(self.evidence(self.obligation_rows[f"B6-R{number:02d}"]["caseEvidence"]["positive"],
            "beta.4 regression ordering", True)["completedAt"], "regression completion") for number in range(1, 9))
        for operation, row in results.items():
            if operation not in UNSUPPORTED | set(BETA4_REGRESSIONS):
                receipt = self.evidence(row["evidence"], "full-catalog ordering", True)
                require(timestamp(receipt["startedAt"], operation) >= regression_end, "full catalog started before the eight beta.4 regressions finished")
        campaign = self.register["campaign"]
        soak = self.evidence(campaign.get("soak"), "soak", True)
        require(soak.get("context") == self.context, "soak belongs to another deployment/campaign")
        start = self.campaign_time(soak.get("startedAt"), "soak start")
        end = self.campaign_time(soak.get("completedAt"), "soak end")
        for operation, row in results.items():
            if operation not in UNSUPPORTED:
                receipt = self.evidence(row["evidence"], "soak functional ordering", True)
                require(timestamp(receipt["completedAt"], operation) <= start, "soak started before functional catalog tests finished")
        for identity, row in self.obligation_rows.items():
            for case_id, reference in row["caseEvidence"].items():
                if row["cases"][case_id] != "implementation":
                    receipt = self.evidence(reference, "soak mandatory-scenario ordering", True)
                    require(timestamp(receipt["completedAt"], case_id) <= start, "soak started before mandatory scenarios finished")
        for case_id, row in historical_results.items():
            if row["state"] == "PASS":
                receipt = self.evidence(row["evidence"], "soak original-corpus ordering", True)
                require(timestamp(receipt["completedAt"], case_id) <= start, "soak started before original-corpus cases finished")
        require((end - start).total_seconds() >= 900, "soak must run at least 15 minutes")
        require(soak.get("afterFunctionalTests") is True and soak.get("healthy") is True and soak.get("recoveryVerified") is True,
                "soak functional ordering, health or recovery evidence missing")
        samples = self.evidence(soak.get("samples"), "soak request samples", True)
        require(isinstance(samples, list) and 0 < len(samples) <= 1000 and soak.get("totalRequests") == len(samples), "invalid soak request population")
        consecutive, times = 0, []
        for sample in samples:
            observed = timestamp(sample.get("at"), "soak sample")
            require(start <= observed <= end, "soak sample is outside soak window")
            times.append(observed)
            require(type(sample.get("latencyMs")) in (int, float) and 0 <= sample["latencyMs"] < float("inf"), "soak latency missing/nonfinite")
            require(type(sample.get("unexpectedFailure")) is bool, "soak outcome missing")
            consecutive = consecutive + 1 if sample["unexpectedFailure"] else 0
            require(consecutive < 5, "soak exceeded five consecutive unexpected failures")
        require(times == sorted(times), "soak request times are not ordered")
        require(all((times[i] - times[i - 2]).total_seconds() >= 1 for i in range(2, len(times))), "soak exceeded two requests per second")
        failures = sum(sample["unexpectedFailure"] for sample in samples)
        require(soak.get("unexpectedFailures") == failures, "soak error count disagrees with samples")
        require(soak.get("unresolvedFailures") == 0, "soak has unresolved failures")
        self.evidence(soak.get("workloadAndRecoveryEvidence"), "soak workload and recovery evidence")
        for field in ("restoration", "independentReview"):
            receipt = self.evidence(campaign.get(field), field, True)
            require(receipt.get("context") == self.context and receipt.get("complete") is True and named(receipt.get("verifier")),
                    f"{field}: complete independent receipt missing")
            self.campaign_time(receipt.get("verifiedAt"), field)
            self.evidence(receipt.get("evidence"), field + " observations")
        review = self.evidence(campaign["independentReview"], "independent review", True)
        restoration = self.evidence(campaign["restoration"], "restoration invariants", True)
        restored_at = timestamp(restoration["verifiedAt"], "restoration verification")
        reviewed_at = timestamp(review["verifiedAt"], "independent review verification")
        require(restored_at >= end, "restoration verification predates functional completion or soak completion")
        require(reviewed_at >= restored_at, "independent review predates restoration verification")
        for identity, row in self.obligation_rows.items():
            closure = self.evidence(row["closure"], f"{identity} final review ordering", True)
            require(reviewed_at >= timestamp(closure["closedAt"], f"{identity} closure"),
                    "independent review predates an individual obligation closure")
        for invariant in ("pausedWorkerPreserved", "savedSenderDisabled", "unrelatedQueuesPreserved", "terminalHistoryPreserved",
                          "temporarySettingsRestored", "campaignJobsStopped", "uncertainOutcomesReconciled", "ownedFixturesRetained"):
            require(restoration.get(invariant) is True, f"restoration: {invariant} has not been verified")
        delivery_owners = {row["owners"][role]["name"] for row in self.obligation_rows.values() for role in ROLES if role != "verifier"}
        require(review["verifier"] not in delivery_owners, "final reviewer is not independent of delivery owners")
        require(review.get("acceptanceIssue") == ISSUES + "219" and review.get("allOriginalCasesReconciled") is True
                and review.get("unresolvedReproducedDefects") == 0 and review.get("missingMandatoryDependencies") == 0
                and review.get("ownerReceivedCompleteReceipt") is True, "#219 full acceptance receipt is incomplete")
        return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("register", nargs="?", type=Path, default=DEFAULT_REGISTER)
    parser.add_argument("--mode", choices=("structure", "acceptance"), default="structure")
    parser.add_argument("--evidence-root", type=Path, help="Private directory containing referenced receipts; no network access is used.")
    args = parser.parse_args()
    try:
        validator = Validator(read_json(args.register), args.evidence_root)
        validator.structure()
        if args.mode == "acceptance" and validator.register["productionReadiness"] != "YES":
            validator.acceptance()
        print(json.dumps({"mode": args.mode, "valid": True, "productionReadiness": validator.register["productionReadiness"],
                          "historicalBlockerRows": len(BLOCKED_KEYS), "obligationCount": len(validator.expected),
                          "checkedEvidenceReferences": validator.checked_references,
                          "meaning": "template structure only; acceptance remains open" if validator.register["productionReadiness"] == "NO"
                                     else "receipt completeness and hashes validated; independent evidence review remains authoritative"}))
        return 0
    except (Invalid, OSError, ValueError, KeyError, TypeError, AttributeError) as exc:
        print(json.dumps({"mode": args.mode, "valid": False, "productionReadiness": "NO", "error": str(exc)}), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

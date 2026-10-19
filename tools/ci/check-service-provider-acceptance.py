#!/usr/bin/env python3
"""Reject absent, failed or skipped required receiver/service/owner acceptance evidence."""

import re
import sys
import xml.etree.ElementTree as ET


def main(path: str) -> None:
    root = ET.parse(path).getroot()
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    results = root.findall("t:Results/t:UnitTestResult", ns)
    if not results or any(result.get("outcome") != "Passed" for result in results):
        raise SystemExit("Required provider acceptance must contain passing results and no failed/skipped cases.")
    counters = root.find("t:ResultSummary/t:Counters", ns)
    if counters is None or int(counters.get("total", "0")) != len(results) or int(counters.get("passed", "0")) != len(results):
        raise SystemExit("Required provider acceptance counters do not prove all selected cases passed.")
    classes = {
        "IncidentReceiverTests": True,
        "IncidentReceiverConformanceTests": False,
        "ServiceIdentityHttpTests": True,
        "ManagedOrchestrationCallbackHttpTests": True,
        "PairingServiceTests": True,
        "PairingEndpointTests": False,
        "PairingUpgradeTests": True,
        "ServiceIdentityMigrationTests": True,
        "WorklogMailDeliveryTests": True,
        "AiInvestigationMcpHttpTests": True,
        "AiInvestigationDispatchRaceTests": True,
        "AiInvestigationDispatchIdempotencyTests": True,
        "OrchestrationConnectionSelectionHttpTests": True,
        "NotificationSummaryPerformanceTests": True,
        "McpGatewayRealAccessPipelineTests": True,
        "IntegrationCredentialPairingTests": True,
        "IntegrationCredentialPairingProviderTests": True,
        "McpHttpHostTests": False,
        "RequestTaskApprovalDecisionProviderTests": True,
        "AuthenticatedRequestApprovalEndpointTests": False,
        "AuthenticatedChangeApprovalTests": True,
        "OperatorDatasetConcurrencyTests": True,
    }
    for name, requires_providers in classes.items():
        names = [result.get("testName", "") for result in results if f".{name}." in result.get("testName", "")]
        if not names:
            raise SystemExit(f"Required acceptance class has no executed cases: {name}")
        if requires_providers:
            providers = {
                match.group(1).lower()
                for test_name in names
                if (match := re.search(r"\bpostgres(?:ql)?:\s*(true|false)\b", test_name, re.IGNORECASE))
            }
            if providers != {"true", "false"}:
                raise SystemExit(f"Required acceptance class lacks actual SQLite and PostgreSQL cases: {name}")
        print(f"{name}: {len(names)} passed; skips=0")
    owner_methods = {
        "McpGatewayRealAccessPipelineTests": (
            "Owner_global_administration_survives_disabled_default_and_no_enabled_organizations",
            "Explicit_owner_grant_uses_real_issuer_and_gateway_to_administer_multiple_organizations",
            "Owner_grant_issuer_requires_current_admin_mcp_purpose_exact_grant_and_explicit_consent",
            "Owner_grant_cached_execution_rechecks_admin_account_expiry_revocation_and_gateway_resource",
            "Owner_can_manage_only_owned_credential_metadata_while_issuance_remains_interactive",
            "Owner_can_register_and_revise_incident_sources_while_ordinary_grants_stay_denied",
        ),
        "IntegrationCredentialPairingTests": (
            "Owner_pairing_requires_explicit_scope_consent_and_replays_the_same_persisted_grant",
            "Owner_pairing_revalidates_current_administrator_before_every_delivery",
            "Setup_verification_cannot_be_upgraded_to_owner_administration",
        ),
        "RequestTaskApprovalDecisionProviderTests": (
            "Signed_and_authenticated_decisions_share_one_terminal_outcome_and_committed_effects",
            "Failed_commit_rolls_back_decision_actor_timeline_and_queued_delivery_before_retry",
            "Timeout_rechecks_selected_candidates_after_a_concurrent_approved_decision",
            "Expired_approvals_reject_both_transports_and_cannot_reopen_after_timeout",
        ),
        "OperatorDatasetConcurrencyTests": (
            "Competing_operator_and_ingestion_batches_preserve_one_case_insensitive_key_and_atomic_rows",
            "Legacy_case_collisions_require_explicit_complete_replacement_and_do_not_poison_later_saves",
            "Duplicate_ingestion_keys_are_rejected_before_any_existing_row_changes",
            "A_failed_batch_cannot_be_replayed_by_a_background_failure_status_save",
        ),
        "AuthenticatedChangeApprovalTests": (
            "Designated_change_approvers_are_actor_bound_and_replays_do_not_duplicate_committed_effects",
            "Signed_and_authenticated_change_reviews_share_one_serialized_transition",
            "Commit_failure_rolls_back_change_review_audit_and_timeline_publication",
            "Lifecycle_and_final_approval_share_one_fence_and_cannot_overwrite_the_winner",
            "A_detail_update_cannot_restore_a_stale_lifecycle_over_a_concurrent_final_approval",
            "Final_approval_queues_each_recipient_once_even_after_replay",
            "A_committed_approval_keeps_actual_smtp_rejection_visible_without_resending_on_review_replay",
            "Final_approval_commit_failure_does_not_release_recipient_notifications",
            "A_missing_or_failed_notification_template_rolls_back_final_approval_until_repaired",
            "Lifecycle_commit_failure_preserves_approval_decisions_without_publishing_reset",
        ),
        "AiInvestigationMcpHttpTests": (
            "Owner_recovers_a_simulated_postcommit_presubmit_queue_without_remote_cancel_or_late_reopening",
        ),
        "AiInvestigationDispatchIdempotencyTests": (
            "Concurrent_first_dispatches_accept_one_bound_invocation_and_one_outbound_request",
            "A_dispatch_key_cannot_select_another_ticket_configuration_actor_or_instruction",
            "Failed_queue_commit_publishes_and_dispatches_nothing_and_retry_creates_one",
        ),
    }
    for class_name, methods in owner_methods.items():
        for method in methods:
            selector = f".{class_name}.{method}("
            names = [result.get("testName", "") for result in results if selector in result.get("testName", "")]
            providers = {
                match.group(1).lower()
                for test_name in names
                if (match := re.search(r"\bpostgres(?:ql)?:\s*(true|false)\b", test_name, re.IGNORECASE))
            }
            if providers != {"true", "false"}:
                raise SystemExit(f"Required owner acceptance lacks actual SQLite and PostgreSQL cases: {class_name}.{method}")
    required_owner_cases = {
        "McpHttpHostTests.Real_owner_http_mcp_host_executes_admin_writes_preserves_ordinary_denials_and_revalidates_empty_scope": ({},),
        "IntegrationCredentialPairingTests.Owner_pairing_revalidates_current_administrator_before_every_delivery": tuple(
            {"postgres": postgres, "issued": issued} for postgres in ("false", "true") for issued in ("false", "true")
        ),
        "AiInvestigationMcpHttpTests.Signed_ai_identity_reports_to_a_real_dispatched_invocation_with_binding_and_duplicate_readbacks": tuple(
            {"postgres": postgres, "ownerMcp": owner} for postgres in ("false", "true") for owner in ("false", "true")
        ),
        "RequestTaskApprovalDecisionProviderTests.Signed_and_authenticated_decisions_share_one_terminal_outcome_and_committed_effects": tuple(
            {"postgres": postgres, "authenticatedWins": authenticated} for postgres in ("false", "true") for authenticated in ("false", "true")
        ),
        "RequestTaskApprovalDecisionProviderTests.Failed_commit_rolls_back_decision_actor_timeline_and_queued_delivery_before_retry": tuple(
            {"postgres": postgres, "approve": approve} for postgres in ("false", "true") for approve in ("false", "true")
        ),
        "AuthenticatedChangeApprovalTests.Commit_failure_rolls_back_change_review_audit_and_timeline_publication": tuple(
            {"postgres": postgres, "signed": signed} for postgres in ("false", "true") for signed in ("false", "true")
        ),
        "AuthenticatedChangeApprovalTests.Lifecycle_and_final_approval_share_one_fence_and_cannot_overwrite_the_winner": tuple(
            {"postgres": postgres, "approvalWins": approval} for postgres in ("false", "true") for approval in ("false", "true")
        ),
        "AuthenticatedChangeApprovalTests.Final_approval_commit_failure_does_not_release_recipient_notifications": tuple(
            {"postgres": postgres, "signed": signed} for postgres in ("false", "true") for signed in ("false", "true")
        ),
        "AuthenticatedChangeApprovalTests.A_missing_or_failed_notification_template_rolls_back_final_approval_until_repaired": tuple(
            {"postgres": postgres, "throws": throws} for postgres in ("false", "true") for throws in ("false", "true")
        ),
    }
    for method, required_arguments in required_owner_cases.items():
        names = [result.get("testName", "") for result in results if f".{method}" in result.get("testName", "")]
        for arguments in required_arguments:
            if not any(all(re.search(rf"\b{argument}:\s*{value}\b", name, re.IGNORECASE)
                           for argument, value in arguments.items()) for name in names):
                raise SystemExit(f"Required owner acceptance case is absent: {method} {arguments}")
    print(f"Required provider acceptance: {len(results)} passed; failures=0; skips=0")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: check-service-provider-acceptance.py <acceptance.trx>")
    main(sys.argv[1])

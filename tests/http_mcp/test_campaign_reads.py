"""Offline harness regression tests; these are not deployed acceptance results."""

import json
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace
from urllib.error import HTTPError

from campaign_reads import (CampaignError, Captures, NoRedirect, Transport, compare,
                            decode_api_read, discovered_values, read_operation, resolve_bindings, run)


CATALOG = json.loads((Path(__file__).resolve().parents[2] /
                     "docs/contracts/mcp-operation-catalog.json").read_text())


class ReadCampaignTests(unittest.TestCase):
    def test_same_run_discovery_resolves_exact_selector_references(self):
        fixture = {"id": "incident", "selectors": {"id": "/items/0/id"}}
        values = discovered_values(fixture, {"items": [{"id": "current-owned-id"}]})
        resolved = resolve_bindings({"request": {"incidentId": "${incident.id}"}}, values)
        self.assertEqual(resolved["request"]["incidentId"], "current-owned-id")

    def test_unknown_or_interpolated_selector_cannot_fall_back_to_stale_id(self):
        for value in ("${deleted.id}", "prefix-${incident.id}", "${incident.id}/other"):
            with self.assertRaises(CampaignError):
                resolve_bindings(value, {"incident.id": "owned"})

    def test_fixture_discovery_rejects_empty_and_nonscalar_selectors(self):
        for value in ("", [], {}, None, True):
            with self.assertRaises(CampaignError):
                discovered_values({"id": "incident", "selectors": {"id": "/id"}}, {"id": value})

    def test_sla_tolerance_preserves_raw_captures_and_compares_stable_fields(self):
        mcp = {"success": True, "data": {"id": "owned", "state": 1,
            "sla": {"responseRemainingSeconds": 100, "resolutionRemainingSeconds": 200}}}
        api = {"id": "owned", "state": 1,
            "sla": {"responseRemainingSeconds": 99, "resolutionRemainingSeconds": 199}}
        checks = [{"pointer": "/id", "expected": "owned"}]
        with self.assertRaises(CampaignError):
            compare(mcp, api, checks)
        compare(mcp, api, checks, 2)
        self.assertEqual(mcp["data"]["sla"]["responseRemainingSeconds"], 100)
        api["state"] = 2
        with self.assertRaises(CampaignError):
            compare(mcp, api, checks, 2)

    def test_sla_tolerance_cannot_hide_large_drift_or_change_asserted_values(self):
        mcp = {"success": True, "data": {"id": "owned",
            "sla": {"responseRemainingSeconds": 100, "resolutionRemainingSeconds": 200}}}
        api = {"id": "owned", "sla": {"responseRemainingSeconds": 90, "resolutionRemainingSeconds": 190}}
        for tolerance in (2, 6, True):
            with self.assertRaises(CampaignError):
                compare(mcp, api, [{"pointer": "/id", "expected": "owned"}], tolerance)
        with self.assertRaises(CampaignError):
            compare(mcp, mcp["data"], [{"pointer": "/sla", "expected": mcp["data"]["sla"]}], 2)

    def test_metrics_text_matches_explicit_mcp_projection(self):
        text = b"# TYPE helpdesk_tasks_in_progress gauge\nhelpdesk_tasks_in_progress 2\n"
        api = decode_api_read("/api/v1/metrics/workflows", text, {"Content-Type": "text/plain; version=0.0.4"})
        compare({"success": True, "data": api}, api, [{"pointer": "/format", "expected": "prometheus"},
            {"pointer": "/text", "expected": text.decode()}])

    def test_non_metrics_text_and_metrics_html_are_rejected(self):
        with self.assertRaises(json.JSONDecodeError):
            decode_api_read("/api/v1/system/version", b"not JSON", {"Content-Type": "text/plain"})
        with self.assertRaises(CampaignError):
            decode_api_read("/api/v1/metrics/workflows", b"<html>login</html>", {"Content-Type": "text/html"})

    def test_empty_log_search_cannot_satisfy_known_warning_assertion(self):
        with self.assertRaises((CampaignError, IndexError)):
            compare({"success": True, "data": {"items": []}}, {"items": []},
                [{"pointer": "/items/0/message", "expected": "Synthetic transport failure"}])

    def case(self):
        return {"id": "owned-incident", "operation": "helpdesk_incidents.get",
                "request": {"incidentId": "owned-fixture"},
                "assertions": [{"pointer": "/id", "expected": "owned-fixture"}]}

    def test_catalog_builds_exact_read_route(self):
        op, args, path = read_operation(CATALOG, self.case())
        self.assertEqual(op["tool"], "helpdesk_incidents")
        self.assertEqual(args, {"operation": "get", "request": {"incidentId": "owned-fixture"}})
        self.assertEqual(path, "/api/v1/incidents/owned-fixture")

    def test_mutation_is_rejected_before_transport(self):
        case = {**self.case(), "operation": "helpdesk_incidents.delete"}
        with self.assertRaises(CampaignError):
            read_operation(CATALOG, case)

    def test_unknown_request_fields_are_rejected(self):
        case = self.case()
        case["request"]["unexpected"] = True
        with self.assertRaises(CampaignError):
            read_operation(CATALOG, case)

    def test_fixture_assertions_are_required(self):
        case = self.case()
        case["assertions"] = []
        with self.assertRaises(CampaignError):
            read_operation(CATALOG, case)

    def test_optional_null_query_is_omitted(self):
        case = {"id": "overdue", "operation": "helpdesk_automation.overdue_tasks",
                "request": {"page": None, "pageSize": 5},
                "assertions": [{"pointer": "/total", "expected": 1}]}
        _, _, path = read_operation(CATALOG, case)
        self.assertEqual(path, "/api/v1/ops/tasks/overdue?pageSize=5")

    def test_dot_selector_cannot_escape_independent_api_route(self):
        case = self.case()
        case["request"]["incidentId"] = ".."
        with self.assertRaises(CampaignError):
            read_operation(CATALOG, case)

    def test_profile_inside_checkout_is_rejected_before_network(self):
        with tempfile.TemporaryDirectory() as temporary:
            with patch("campaign_reads.Transport") as transport:
                with self.assertRaises(CampaignError):
                    run(SimpleNamespace(output=Path(temporary) / "new",
                                        profile=Path(__file__).resolve()))
                transport.assert_not_called()

    def test_api_success_cannot_replace_failed_mcp(self):
        with self.assertRaises(CampaignError):
            compare({"success": False, "data": {"id": "owned-fixture"}},
                    {"id": "owned-fixture"}, self.case()["assertions"])

    def test_different_mcp_projection_fails(self):
        with self.assertRaises(CampaignError):
            compare({"success": True, "data": {"id": "wrong"}},
                    {"id": "owned-fixture"}, self.case()["assertions"])

    def test_empty_reads_do_not_count_as_fixture_coverage(self):
        for value in (None, [], {}):
            with self.assertRaises(CampaignError):
                compare({"success": True, "data": value}, value, [{"pointer": "", "expected": value}])

    def test_boolean_does_not_match_integer_assertion(self):
        with self.assertRaises(CampaignError):
            compare({"success": True, "data": {"n": 1}}, {"n": 1},
                    [{"pointer": "/n", "expected": True}])

    def test_matching_mcp_api_and_typed_fixture(self):
        compare({"success": True, "data": {"id": "owned-fixture"}},
                {"id": "owned-fixture"}, self.case()["assertions"])

    def test_redaction_covers_mcp_duplicate_json_and_signed_url(self):
        with tempfile.TemporaryDirectory() as temporary:
            captures = Captures(Path(temporary), ["synthetic-sensitive-value"])
            ref = captures.write("test", {"accessToken": "synthetic-sensitive-value",
                "content": [{"text": json.dumps({"signingSecret": "other-synthetic-secret"})}],
                "url": "https://example.invalid/image?token=synthetic-token&size=1",
                "message": "synthetic-sensitive-value"})
            raw = (Path(temporary) / ref["path"]).read_text()
            for value in ("synthetic-sensitive-value", "other-synthetic-secret", "synthetic-token"):
                self.assertNotIn(value, raw)

    def test_plain_http_and_noncanonical_resources_are_refused(self):
        for uri in ("http://example.invalid/mcp", "https://example.invalid/other/mcp",
                    "https://example.invalid/mcp?token=x", "https://user@example.invalid/mcp"):
            with self.assertRaises(CampaignError):
                Transport(uri, "synthetic", None)

    def test_duplicate_json_text_decodes_unicode_escaped_secret_before_redaction(self):
        with tempfile.TemporaryDirectory() as temporary:
            captures = Captures(Path(temporary), [])
            duplicate = json.dumps({"activationToken": "synthetic+activation/value="})
            duplicate = duplicate.replace("+", "\\u002B").replace("/", "\\u002F")
            ref = captures.write("test", {"id": "synthetic-rpc-id",
                "content": [{"type": "text", "text": duplicate}]})
            captured = json.loads((Path(temporary) / ref["path"]).read_text())
            self.assertEqual(captured["id"], "synthetic-rpc-id")
            nested = json.loads(captured["content"][0]["text"])
            self.assertEqual(nested["activationToken"], "[REDACTED]")

    def test_html_escaped_signed_url_is_redacted_without_losing_link_markup(self):
        with tempfile.TemporaryDirectory() as temporary:
            captures = Captures(Path(temporary), [])
            ref = captures.write("test", {"html":
                '<a href="https://example.invalid/view-ticket/owned?email=reader%40example.invalid'
                '&amp;token=synthetic-private-link">View</a>'})
            captured = json.loads((Path(temporary) / ref["path"]).read_text())
            self.assertNotIn("synthetic-private-link", captured["html"])
            self.assertIn('&amp;token=[REDACTED]">View</a>', captured["html"])

    def test_redirect_cannot_forward_credential_to_other_origin(self):
        with self.assertRaises(CampaignError):
            NoRedirect().redirect_request(None, None, 302, "", {}, "https://other.invalid/mcp")

    def test_http_denial_is_captured_with_secret_redacted(self):
        with tempfile.TemporaryDirectory() as temporary:
            captures = Captures(Path(temporary), ["synthetic-credential"])
            transport = Transport("https://example.invalid/mcp", "synthetic-credential", captures)
            error = HTTPError("https://example.invalid/mcp", 403, "denied", {},
                              io.BytesIO(b'{"accessToken":"synthetic-response-secret"}'))
            with patch.object(transport.opener, "open", side_effect=error):
                with self.assertRaises(CampaignError):
                    transport.request(transport.resource, "POST", b"{}", {})
            files = list(Path(temporary).glob("*-http-error.json"))
            self.assertEqual(len(files), 1)
            captured = files[0].read_text()
            self.assertNotIn("synthetic-response-secret", captured)
            self.assertEqual(json.loads(captured)["httpStatus"], 403)


if __name__ == "__main__":
    unittest.main()

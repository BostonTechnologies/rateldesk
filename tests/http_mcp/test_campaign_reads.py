"""Offline harness regression tests; these are not deployed acceptance results."""

import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

from campaign_reads import CampaignError, Captures, NoRedirect, Transport, compare, read_operation, run


CATALOG = json.loads((Path(__file__).resolve().parents[2] /
                     "docs/contracts/mcp-operation-catalog.json").read_text())


class ReadCampaignTests(unittest.TestCase):
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

    def test_redirect_cannot_forward_credential_to_other_origin(self):
        with self.assertRaises(CampaignError):
            NoRedirect().redirect_request(None, None, 302, "", {}, "https://other.invalid/mcp")


if __name__ == "__main__":
    unittest.main()

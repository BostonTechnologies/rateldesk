"""Offline checks for exact body generation and transport outcome classification."""

import json
import unittest

from campaign_reads import CampaignError
from campaign_transport import boundary_format, decode_response, expected_result, initialize_body


class TransportCampaignTests(unittest.TestCase):
    def test_release_manifest_selects_exact_supported_summary_format(self):
        for beta in (11, 12):
            version = f"0.1.2-beta.{beta}"
            manifest = {"version": version, "tag": "v" + version, "sourceRevision": "a" * 40}
            self.assertEqual(boundary_format(manifest), f"rateldesk-beta{beta}-body-boundary/v1")
        for manifest in (
            {"version": "0.1.2-beta.13", "tag": "v0.1.2-beta.13", "sourceRevision": "a" * 40},
            {"version": "0.1.2-beta.12", "tag": "v0.1.2-beta.11", "sourceRevision": "a" * 40},
            {"version": "0.1.2-beta.12", "tag": "v0.1.2-beta.12", "sourceRevision": "main"},
        ):
            with self.assertRaises(CampaignError):
                boundary_format(manifest)

    def test_generated_control_is_valid_json_at_exact_requested_size(self):
        raw = initialize_body(4096, "synthetic-rpc")
        self.assertEqual(len(raw), 4096)
        message = json.loads(raw)
        self.assertEqual(message["id"], "synthetic-rpc")
        self.assertEqual(message["method"], "initialize")
        self.assertNotIn("tools/call", raw.decode())
        with self.assertRaises(CampaignError):
            initialize_body(1, "synthetic-rpc")

    def test_at_limit_requires_actual_initialize_success_and_original_rpc_id(self):
        response = {"httpStatus": 200, "body": {"id": "synthetic-rpc", "result": {"serverInfo": {"name": "fixture"}}}}
        self.assertTrue(expected_result(response, 4096, 4096, "synthetic-rpc"))
        self.assertFalse(expected_result(response, 4096, 4096, "other-rpc"))
        self.assertFalse(expected_result({"httpStatus": 200, "body": {"error": {}}}, 4096, 4096, "synthetic-rpc"))

    def test_oversize_requires_documented_body_413_not_proxy_or_reset(self):
        response = {"httpStatus": 413, "contentType": "application/problem+json", "body": {"status": 413, "code": "request_too_large", "limit": "body", "maximumBodyBytes": 4096}}
        self.assertTrue(expected_result(response, 4097, 4096, "synthetic-rpc"))
        self.assertFalse(expected_result({"httpStatus": 413, "body": {}}, 4097, 4096, "synthetic-rpc"))
        self.assertFalse(expected_result({"transportErrorType": "ConnectionResetError"}, 4097, 4096, "synthetic-rpc"))
        response["contentType"] = "text/html"
        self.assertFalse(expected_result(response, 4097, 4096, "synthetic-rpc"))
        response["contentType"] = "application/problem+json; charset=utf-8"
        self.assertTrue(expected_result(response, 4097, 4096, "synthetic-rpc"))
        response["body"]["limit"] = "transport"
        self.assertFalse(expected_result(response, 4097, 4096, "synthetic-rpc"))

    def test_missing_null_or_non_string_media_type_is_a_failed_probe(self):
        response = {"httpStatus": 413, "body": {"status": 413, "code": "request_too_large",
                    "limit": "body", "maximumBodyBytes": 4096}}
        self.assertFalse(expected_result(response, 4097, 4096, "synthetic-rpc"))
        for content_type in (None, 413, [], {}):
            with self.subTest(content_type=content_type):
                response["contentType"] = content_type
                self.assertFalse(expected_result(response, 4097, 4096, "synthetic-rpc"))

    def test_sse_response_selects_the_matching_rpc(self):
        raw = b'data: {"id":"other","result":{}}\n\ndata: {"id":"synthetic-rpc","result":{"serverInfo":{"name":"fixture"}}}\n'
        self.assertEqual(decode_response(raw, "synthetic-rpc")["id"], "synthetic-rpc")


if __name__ == "__main__":
    unittest.main()

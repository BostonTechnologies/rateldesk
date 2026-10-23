#!/usr/bin/env python3
"""Keep public consent diagnostics useful without disclosing private subprocess output."""
from contextlib import redirect_stdout
import importlib.util
import io
import json
from pathlib import Path
import re
import subprocess
from types import SimpleNamespace
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location("host_lifecycle", Path(__file__).with_name("run-mcp-host-lifecycle-acceptance.py"))
lifecycle = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(lifecycle)


def diagnostic_record(value):
    return ("Disposable browser consent did not complete: " + json.dumps(value) +
            "; no credential or page content was recorded.\n").encode()


class BrowserConsentDiagnosticsTests(unittest.TestCase):
    def test_failed_browser_command_reports_only_safe_fields_and_still_raises(self):
        secret = "synthetic-private-password-and-code"
        failure = lifecycle.CommandFailure(["node"], subprocess.CompletedProcess(["node"], 1,
            stdout=secret.encode(), stderr=diagnostic_record({
                "stage": "connection review request", "error": "TimeoutError", "codeRetained": True,
                "reviewEnabled": True, "dialogCount": 0, "alertCount": 1, "atLogin": False,
                "atHome": False, "loginErrorVisible": False, "loginInteractive": "true", "unexpected": secret,
            })))
        fixture = SimpleNamespace(origin="https://desk.fixture.test", email="fixture@example.test",
            password=secret, organization="synthetic-organization")
        output = io.StringIO()
        with patch.object(lifecycle, "checked", side_effect=failure), redirect_stdout(output):
            with self.assertRaises(lifecycle.CommandFailure) as raised:
                lifecycle.PreservedFixture.browser_consent(fixture, "ABCD-EFGH", "verifier")
        self.assertIs(failure, raised.exception)
        rendered = output.getvalue()
        self.assertIn('"stage": "connection review request"', rendered)
        self.assertIn('"error": "TimeoutError"', rendered)
        self.assertIn('"dialogCount": 0', rendered)
        self.assertIn('"codeRetained": true', rendered)
        self.assertIn('"loginInteractive": true', rendered)
        for private in (secret, fixture.origin, fixture.email, fixture.organization, "ABCD-EFGH", "unexpected"):
            self.assertNotIn(private, rendered)

    def test_untrusted_strings_and_invalid_observation_types_are_not_published(self):
        secret = "rdk_synthetic_private_value"
        result = lifecycle.browser_consent_diagnostic(diagnostic_record({
            "stage": secret, "error": secret, "codeRetained": secret, "reviewEnabled": 1,
            "dialogCount": secret, "alertCount": True, "atLogin": secret, "atHome": [],
            "loginErrorVisible": {}, "loginInteractive": secret,
        }))
        self.assertEqual({"stage": "unknown", "error": "Other"}, result)
        for value in (-1, 257, 1.5, None):
            with self.subTest(value=value):
                self.assertNotIn("dialogCount", lifecycle.browser_consent_diagnostic(diagnostic_record({"dialogCount": value})))

    def test_missing_malformed_or_out_of_bound_records_do_not_echo_raw_output(self):
        valid = diagnostic_record({"stage": "bounded input", "error": "Error"})
        for detail in (b"private unstructured stderr", valid + b"x" * 16384,
                       b"Disposable browser consent did not complete: {broken}; no credential or page content was recorded.\n",
                       diagnostic_record(["private-array"])):
            with self.subTest(detail_length=len(detail)):
                self.assertEqual({"stage": "unavailable", "error": "unavailable"}, lifecycle.browser_consent_diagnostic(detail))

    def test_every_fixed_browser_stage_has_an_explicit_public_allowlist_entry(self):
        script = Path(__file__).with_name("mcp-host-browser-consent.mjs").read_text()
        stages = set(re.findall(r"\bstage\s*=\s*'([^']+)'", script))
        self.assertTrue(stages)
        self.assertEqual(stages, lifecycle.BROWSER_CONSENT_STAGES)


if __name__ == "__main__":
    unittest.main()

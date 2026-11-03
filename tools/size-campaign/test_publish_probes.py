"""Evidence must distinguish failed discovery from a compatible candidate."""

import importlib.util
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("publish_probes", Path(__file__).with_name("publish-probes.py"))
probes = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(probes)


class PublishEvidenceTests(unittest.TestCase):
    def test_profiles_use_the_common_source_timestamp_and_explicit_publish_flags(self):
        metadata = {"version": "0.0.0-test", "sourceRevision": "a" * 40,
                    "startedAt": "2026-01-02T00:00:00Z", "buildTimestamp": "2026-01-01T00:00:00Z"}
        for mode in ("baseline", "portable-symbols", "trimmed", "aot"):
            command, _ = probes.base_command("stdio", mode, Path("/tmp/candidate"), metadata)
            self.assertIn("-p:BuildTimestamp=2026-01-01T00:00:00Z", command)
            self.assertNotIn("-p:BuildTimestamp=2026-01-02T00:00:00Z", command)
            self.assertIn("-p:PublishTrimmed=" + str(mode in ("trimmed", "aot")).lower(), command)
            self.assertIn("-p:PublishAot=" + str(mode == "aot").lower(), command)

    def test_il_diagnostics_include_warning_and_error_without_double_counting_identical_lines(self):
        warning = "Source.cs(10,5): warning IL2026: Reflective call [App.csproj]"
        error = "Source.cs(12,5): error IL3050: Dynamic call [App.csproj]"
        report = probes.diagnostics(warning + "\n" + warning + "\n" + error + "\n0 Warning(s)")
        self.assertEqual(2, report["uniqueDiagnosticCount"])
        self.assertEqual({"IL2026": 1, "IL3050": 1}, report["byCode"])

    def test_timeout_and_missing_executable_cannot_report_published(self):
        for status in ("TIMED_OUT", "BLOCKED"):
            self.assertFalse(probes.completed_ok({"executionStatus": status, "exitCode": None}))
            self.assertEqual(status, probes.publish_status({"executionStatus": status, "exitCode": None}))
        self.assertEqual("PUBLISH_FAILED", probes.publish_status({"executionStatus": "COMPLETED", "exitCode": 1}))

    def test_runtime_and_libc_are_explicit_and_self_contained_is_not_an_aspnet_image_claim(self):
        metadata = {"version": "0.0.0-test", "sourceRevision": "a" * 40, "startedAt": "2026-01-01T00:00:00Z"}
        with tempfile.TemporaryDirectory() as temporary:
            for target, expected in (("api", "linux-x64"), ("web", "linux-musl-x64"), ("http", "linux-musl-x64")):
                command, profile = probes.base_command(target, "rid-specific", Path(temporary), metadata)
                self.assertEqual(expected, profile["runtimeIdentifier"])
                self.assertEqual("framework-dependent", profile["deploymentMode"])
                self.assertEqual("false", command[command.index("--self-contained") + 1])
            command, profile = probes.base_command("http", "aot", Path(temporary), metadata)
            self.assertFalse(profile["shippingContainerEquivalent"])
            self.assertEqual("native-aot", profile["deploymentMode"])
            self.assertIn("-p:SuppressTrimAnalysisWarnings=false", command)
            self.assertNotIn("-p:NoWarn", command)

    def test_analysis_does_not_enable_production_trimming(self):
        metadata = {"version": "0.0.0-test", "sourceRevision": "a" * 40, "startedAt": "2026-01-01T00:00:00Z"}
        command, profile = probes.base_command("web", "analysis", Path("/tmp/candidate"), metadata)
        self.assertIn("-p:EnableTrimAnalyzer=true", command)
        self.assertNotIn("-p:PublishTrimmed=true", command)
        self.assertNotIn("-p:PublishAot=true", command)
        self.assertEqual("framework-dependent", profile["deploymentMode"])


if __name__ == "__main__":
    unittest.main()

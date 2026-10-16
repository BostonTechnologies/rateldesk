#!/usr/bin/env python3
"""Fast launcher contracts; no running services, credentials, or image downloads."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("mcp_launcher", ROOT / "tools/mcp/setup-http-mcp.py")
launcher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(launcher)


class DiscoveryDocker:
    def __init__(self, metadata):
        self.metadata = metadata

    def run(self, args, **kwargs):
        if args[0] == "ps":
            return b'"one"\n'
        raise AssertionError(args)

    def inspect(self, identifier):
        return self.metadata


class LauncherContracts(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary.name)
        self.source = self.directory / "compose.json"
        self.source.write_text('{"services":{}}')
        self.args = launcher.parser().parse_args(["--compose-file", str(self.source), "--project", "fixture",
            "--manager", "external-compose"])

    def tearDown(self):
        self.temporary.cleanup()

    def deployment(self):
        return launcher.Deployment(self.args, DiscoveryDocker({}))

    def test_json_environment_retains_equals_multiline_and_empty(self):
        values = ["ApiBaseUrl=http://api:8222/", "Helpdesk__Mcp__Outbound__IntegrationCredential=a=b\nsecond line",
                  "Helpdesk__Mcp__Outbound__CredentialMode="]
        self.assertEqual(launcher.relevant_environment(values), values)
        self.assertEqual(launcher.environment_map(values)["Helpdesk__Mcp__Outbound__IntegrationCredential"], "a=b\nsecond line")

    def test_duplicate_docker_environment_is_rejected(self):
        with self.assertRaisesRegex(launcher.Prerequisite, "environment_duplicate"):
            launcher.environment_map(["ApiBaseUrl=http://one/", "ApiBaseUrl=http://two/"])

    def test_case_and_colon_alias_duplicates_are_rejected_in_both_forms(self):
        for values in (["Helpdesk__Mcp__Instance=first", "helpdesk:MCP:instance=second"],
                       {"Helpdesk__Mcp__Instance": "first", "helpdesk:MCP:instance": "second"}):
            with self.assertRaisesRegex(launcher.Prerequisite, "environment_duplicate"):
                launcher.environment_map(values)

    def test_environment_layering_retains_explicit_empty_and_removes_unset(self):
        effective = launcher.effective_environment(["DOTNET_ENVIRONMENT=Staging", "Helpdesk__Mcp__Instance=old"],
            {"DOTNET_ENVIRONMENT": None, "Helpdesk:Mcp:Instance": "", "ASPNETCORE_ENVIRONMENT": "Preview"})
        self.assertNotIn("dotnet_environment", effective)
        self.assertEqual(effective["helpdesk__mcp__instance"], "")
        self.assertEqual(effective["aspnetcore_environment"], "Preview")

    def test_oversized_environment_is_rejected_without_echoing_value(self):
        with self.assertRaises(launcher.Prerequisite) as failure:
            launcher.environment_map(["Helpdesk__Mcp__Outbound__IntegrationCredential=" + "privatefixture" * 6000])
        self.assertNotIn("privatefixture", str(failure.exception))

    def test_unknown_ownership_is_not_release_owned(self):
        self.args.manager = None
        with patch("sys.stdin.isatty", return_value=False), self.assertRaisesRegex(launcher.Prerequisite, "deployment_manager_unknown"):
            self.deployment()
        self.assertFalse((self.directory / ".rateldesk-http-mcp-fixture").exists())

    def test_explicit_source_order_and_interpolation_files_remain_ordered(self):
        override, first, second = [self.directory / name for name in ("override.json", "one.env", "two.env")]
        for path in (override, first, second):
            path.write_text("{}" if path.suffix == ".json" else "VALUE=fixture\n")
        self.args.compose_file.append(str(override))
        self.args.env_file = [str(first), str(second)]
        deployment = self.deployment()
        self.assertEqual(deployment.compose[-4:], ["-f", str(self.source), "-f", str(override)])
        self.assertLess(deployment.compose.index(str(first)), deployment.compose.index(str(second)))

    def test_missing_and_symlink_sources_fail_before_output_creation(self):
        for path in (self.directory / "missing.json", self.directory / "linked.json"):
            if path.name.startswith("linked"):
                path.symlink_to(self.source)
            self.args.compose_file = [str(path)]
            with self.assertRaisesRegex(launcher.Prerequisite, "source_unreadable"):
                self.deployment()
        self.assertFalse((self.directory / ".rateldesk-http-mcp-fixture").exists())

    def test_private_output_does_not_repair_existing_world_readable_storage(self):
        output = self.directory / "existing"
        output.mkdir(mode=0o755)
        output.chmod(0o755)
        with self.assertRaisesRegex(launcher.Prerequisite, "storage_permissions"):
            launcher.private_directory(output)
        self.assertEqual(output.stat().st_mode & 0o777, 0o755)

    def test_atomic_state_is_private_and_does_not_follow_symlink(self):
        output = launcher.private_directory(self.directory / "private")
        target = output / "state.json"
        launcher.write_private(target, {"phase": "saved-apply-pending"})
        self.assertEqual(target.stat().st_mode & 0o777, 0o600)
        self.assertEqual(launcher.read_private_json(target)["phase"], "saved-apply-pending")
        target.unlink()
        target.symlink_to(self.source)
        with self.assertRaisesRegex(launcher.Prerequisite, "storage_symlink"):
            launcher.write_private(target, {"phase": "unapproved"})
        self.assertEqual(self.source.read_text(), '{"services":{}}')

    def test_verify_in_new_instance_accepts_unchanged_sources_and_rejects_separate_change(self):
        first = self.deployment()
        records = [launcher.file_record(self.source)]
        launcher.write_private(first.output / "launcher-state.json", {"sources": records})
        # A genuinely new object and state-file read model a fresh invocation.
        second = self.deployment()
        second.validate_resume_sources([launcher.file_record(self.source)], True)
        self.source.write_text('{"services":{"changed":{}}}')
        third = self.deployment()
        with self.assertRaisesRegex(launcher.Prerequisite, "approved_sources_changed"):
            third.validate_resume_sources([launcher.file_record(self.source)], True)

    def test_external_manager_only_accepts_exact_reviewed_single_document_replacement(self):
        deployment = self.deployment()
        old = [launcher.file_record(self.source)]
        complete = b'{"services":{"preserved":{}}}\n'
        digest = launcher.hashlib.sha256(complete).hexdigest()
        launcher.write_private(deployment.output / "launcher-state.json", {"sources": old, "deploymentSha256": digest})
        self.source.write_bytes(complete)
        deployment.validate_resume_sources([launcher.file_record(self.source)], True)
        self.source.write_bytes(complete + b" ")
        with self.assertRaisesRegex(launcher.Prerequisite, "approved_sources_changed"):
            deployment.validate_resume_sources([launcher.file_record(self.source)], True)

    def test_external_manager_apply_does_not_call_docker(self):
        deployment = self.deployment()
        with self.assertRaisesRegex(launcher.Prerequisite, "manager_apply_required"):
            launcher.apply_saved(deployment)

    def test_export_mutation_rejected_before_apply(self):
        deployment = self.deployment()
        content = b'{"services":{}}\n'
        launcher.write_private(deployment.output / "deployment.json", content)
        state = {"exportHashes": {}, "deploymentSha256": launcher.hashlib.sha256(content).hexdigest()}
        launcher.check_saved_exports(deployment, state)
        launcher.write_private(deployment.output / "deployment.json", content + b" ")
        with self.assertRaisesRegex(launcher.Prerequisite, "approved_export_changed"):
            launcher.check_saved_exports(deployment, state)

    def test_unchanged_files_with_changed_interpolation_context_require_review(self):
        deployment = self.deployment()
        records = [launcher.file_record(self.source)]
        deployment.proposal = {"x-rateldesk-source-files": records}
        launcher.write_private(deployment.output / "effective-deployment.json", {"environment": "reviewed"})
        digest = launcher.hashlib.sha256((deployment.output / "effective-deployment.json").read_bytes()).hexdigest()
        state = {"sources": records, "sourceHash": digest.upper()}
        launcher.check_effective_context(deployment, state)
        launcher.write_private(deployment.output / "effective-deployment.json", {"environment": "changed"})
        with self.assertRaisesRegex(launcher.Prerequisite, "approved_context_changed"):
            launcher.check_effective_context(deployment, state)

    def test_image_environment_selects_correct_provider_and_explicit_configuration_path(self):
        deployment = self.deployment()
        deployment.provider_source_records = {}
        providers = {"/app/appsettings.json": b'{"Helpdesk":{"Mcp":{"ConfigurationPath":"/run/old.json"}}}',
                     "/app/appsettings.Preview.json": b'{"Helpdesk":{"Mcp":{"ConfigurationPath":"/run/reviewed.json"}}}',
                     "/run/reviewed.json": b'{"ApiBaseUrl":"http://private-api:8222/","CredentialMode":"gateway"}'}
        class ImageDocker:
            def run(self, arguments, **kwargs):
                if arguments[0] == "create":
                    return b"a" * 64
                if arguments[0] == "rm":
                    return b"removed"
                if arguments[0] == "cp":
                    path = arguments[1].split(":", 1)[1]
                    if path not in providers:
                        return None
                    Path(arguments[2]).write_bytes(providers[path])
                    return b""
                raise AssertionError(arguments)
        deployment.docker = ImageDocker()
        result = deployment.configuration_providers("mcp-http", {"environment": {"DOTNET_ENVIRONMENT": None}},
            "fixture", [], image_environment=["DOTNET_ENVIRONMENT=Staging", "ASPNETCORE_ENVIRONMENT=Preview"])
        self.assertEqual([provider["path"] for provider in result],
            ["/app/appsettings.json", "/app/appsettings.Preview.json", "/run/reviewed.json"])

    def test_shipped_release_metadata_discovers_fresh_base_without_url_placeholder(self):
        base = self.directory / "docker-compose.release.yml"
        base.write_text('{"services":{}}')
        (self.directory / "setup-release.json").write_text(json.dumps({"schemaVersion": 1, "version": "0.1.2-beta.1",
            "composeFile": base.name, "projectName": "rateldesk", "manager": "release-compose",
            "image": "ghcr.io/bostontechnologies/rateldesk-mcp-http:0.1.2-beta.1"}))
        args = launcher.parser().parse_args([])
        with patch.object(launcher, "__file__", str(self.directory / "setup-http-mcp.py")):
            deployment = launcher.Deployment(args, DiscoveryDocker({}))
        self.assertEqual(deployment.sources, [base])
        self.assertEqual(deployment.project, "rateldesk")
        self.assertTrue(deployment.foundation)
        contents = (deployment.output / "launcher.env").read_text()
        self.assertEqual(contents, "RATELDESK_VERSION=0.1.2-beta.1\n")
        self.assertNotIn("ORIGIN", contents)

    def test_only_explicit_downstream_secret_mount_is_inherited_readonly(self):
        deployment = self.deployment()
        deployment.image = "fixture"
        deployment.storage = {}
        digest = "b" * 64
        calls = []
        class SecretDocker:
            def run(self, arguments, **kwargs):
                calls.append(arguments)
                return (digest + "  /run/secrets/downstream\n").encode()
        deployment.docker = SecretDocker()
        proposal = {"services": {"mcp-http": {"environment": {}, "volumes": [
            {"type": "bind", "source": str(self.directory / "downstream.secret"), "target": "/run/secrets/downstream"},
            {"type": "bind", "source": "/unrelated/private", "target": "/unrelated"}]}},
            "x-rateldesk-image-environment": {"mcp-http": []},
            "x-rateldesk-protected-configuration": {"mcp-http": [{"path": "/run/config.json", "content": '{"authentikAppPasswordFile":"/run/secrets/downstream"}'}]}}
        deployment.capture_secret_mounts(proposal)
        self.assertEqual(len(deployment.secret_mounts), 1)
        self.assertEqual(deployment.secret_mounts[0]["sha256"], digest)
        command = launcher.tooling_arguments(deployment)
        self.assertTrue(any("target=/run/secrets/downstream,readonly" in value for value in command))
        self.assertNotIn("/unrelated/private", str(command))
        self.assertIn("10001:10001", calls[0])

    def test_stale_lower_secret_reference_is_not_required_after_mode_or_source_replacement(self):
        proposal = {"services": {"mcp-http": {"environment": {"Helpdesk__Mcp__Outbound__CredentialMode": "gateway"}}},
            "x-rateldesk-image-environment": {"mcp-http": []},
            "x-rateldesk-protected-configuration": {"mcp-http": [{"path": "/run/config.json",
                "content": '{"credentialMode":"authentik","authentikAppPasswordFile":"/run/stale/missing"}'}]}}
        self.assertEqual(launcher.selected_secret_references(proposal), set())
        proposal["services"]["mcp-http"]["environment"] = {"Helpdesk__Mcp__Outbound__AuthentikAppPasswordFile": ""}
        self.assertEqual(launcher.selected_secret_references(proposal), set())
        proposal["services"]["mcp-http"]["environment"] = {"Helpdesk__Mcp__Outbound__AuthentikAppPassword": "synthetic-replacement"}
        self.assertEqual(launcher.selected_secret_references(proposal), set())

    def test_service_command_environment_overrides_image_environment_provider(self):
        deployment = self.deployment()
        deployment.provider_source_records = {}
        copied = []
        class CommandDocker:
            def run(self, arguments, **kwargs):
                if arguments[0] == "create":
                    return b"a" * 64
                if arguments[0] == "rm":
                    return b"removed"
                if arguments[0] == "cp":
                    copied.append(arguments[1])
                    return None
                raise AssertionError(arguments)
        deployment.docker = CommandDocker()
        deployment.configuration_providers("web", {"environment": {}, "command": ["--environment=Reviewed"]},
            "fixture", [], image_environment=["DOTNET_ENVIRONMENT=Unreviewed"])
        self.assertTrue(any("appsettings.Reviewed.json" in value for value in copied))
        self.assertFalse(any("appsettings.Unreviewed.json" in value for value in copied))

    def test_forwarded_options_are_literal_arguments(self):
        args = launcher.parser().parse_args(["--", "--web-url", "https://desk.example.test", "--instance", "stage-$(touch ignored)"])
        self.assertEqual(args.wizard_options[1:], ["--web-url", "https://desk.example.test", "--instance", "stage-$(touch ignored)"])
        self.assertFalse((self.directory / "ignored").exists())

    def test_storage_descriptors_cannot_smuggle_docker_mount_options(self):
        with self.assertRaisesRegex(launcher.Prerequisite, "storage_source"):
            launcher.mount_argument({"type": "volume", "source": "fixture,readonly", "target": "/output"})

    def test_malformed_display_metadata_never_prints_control_sequences(self):
        with self.assertRaisesRegex(launcher.Prerequisite, "deployment_metadata"):
            launcher.safe_label("instance\nsecret")


class ComposeContract(unittest.TestCase):
    def test_full_manager_document_keeps_unrelated_services_envfiles_and_source_order(self):
        # Compose is the only parser/merge engine. This fast recipe contract
        # needs the CLI plugin, but does not contact a daemon or run an image.
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            environment = directory / "service.env"
            environment.write_text("UNCHANGED=saved\n")
            base = directory / "base.json"
            override = directory / "override.json"
            base.write_text(json.dumps({"services": {
                "database": {"image": "postgres:17", "volumes": ["database:/var/lib/postgresql/data"]},
                "mcp-http": {"image": "mcp:fixture", "env_file": [str(environment)],
                             "environment": {"INSTANCE": "${INSTANCE:-custom-label}", "WINNER": "first"}}
            }, "volumes": {"database": {}}}))
            override.write_text(json.dumps({"services": {"mcp-http": {"environment": {"WINNER": "reviewed"}}}}))
            rendered = subprocess.run(["docker", "compose", "-f", str(base), "-f", str(override),
                "config", "--no-interpolate", "--no-env-resolution", "--format", "json"],
                capture_output=True, timeout=20)
            self.assertEqual(rendered.returncode, 0, "A supported Docker Compose v2 plugin is required for launcher recipe contracts")
            complete = json.loads(rendered.stdout)
            self.assertIn("database", complete["services"])
            self.assertIn("database", complete["volumes"])
            mcp = complete["services"]["mcp-http"]
            values = launcher.environment_map(mcp["environment"])
            self.assertEqual(values["WINNER"], "reviewed")
            self.assertEqual(values["INSTANCE"], "${INSTANCE:-custom-label}")
            self.assertEqual(mcp["env_file"][0]["path"], str(environment))


if __name__ == "__main__":
    unittest.main()

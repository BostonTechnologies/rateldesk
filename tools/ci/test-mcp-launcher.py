#!/usr/bin/env python3
"""Fast launcher contracts; no running services, credentials, or image downloads."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import shlex
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

    def test_approved_legacy_removal_changes_complete_source_without_flattening_expressions_or_shared_definitions(self):
        source = {"services": {"mcp-http": {"configs": [{"source": "${OUTBOUND_ALIAS}", "target": "${OUTBOUND_PATH}"},
                    {"source": "other", "target": "/run/rateldesk-mcp-extra/config.json"}],
                    "env_file": [{"path": "${INSTANCE_ENV}", "required": False}]},
                "api": {"configs": [{"source": "${OUTBOUND_ALIAS}", "target": "/unrelated/config.json"}]}},
            "configs": {"legacy": {"file": "${OUTBOUND_FILE}"}}, "volumes": {"database": {"external": True}}}
        rendered = {"services": {"mcp-http": {"configs": [{"source": "legacy", "target": "/run//rateldesk-mcp/./config.json"},
            {"source": "other", "target": "/run/rateldesk-mcp-extra/config.json"}]}}}
        patch_document = {"x-rateldesk-mount-migrations": [{"service": "mcp-http", "kind": "configs", "source": "legacy",
            "target": "/run/rateldesk-mcp/config.json"}]}
        before = json.dumps(source)
        complete_source = launcher.migrated_compose_source(source, rendered, patch_document)
        self.assertEqual(complete_source["services"]["mcp-http"]["configs"], [{"source": "other", "target": "/run/rateldesk-mcp-extra/config.json"}])
        self.assertEqual(complete_source["services"]["mcp-http"]["env_file"], source["services"]["mcp-http"]["env_file"])
        self.assertEqual(complete_source["services"]["api"], source["services"]["api"])
        self.assertEqual(complete_source["configs"], source["configs"])
        self.assertEqual(complete_source["volumes"], source["volumes"])
        self.assertEqual(json.dumps(source), before)

    def test_legacy_removal_fails_closed_for_changed_missing_duplicate_or_unreviewed_mapping(self):
        migration = {"service": "mcp-http", "kind": "configs", "source": "legacy", "target": "/run/rateldesk-mcp/config.json"}
        mapping = {"source": "legacy", "target": migration["target"]}
        for actual, original, changes in (([], [], [migration]), ([mapping, mapping], [mapping, mapping], [migration]),
                ([mapping], [], [migration]), ([mapping], [mapping], [{**migration, "kind": "secrets"}])):
            with self.subTest(actual=actual, original=original, changes=changes), self.assertRaisesRegex(launcher.Prerequisite, "approved_mount_migration"):
                launcher.migrated_compose_source({"services": {"mcp-http": {"configs": original}}},
                    {"services": {"mcp-http": {"configs": actual}}}, {"x-rateldesk-mount-migrations": changes})

    def test_already_migrated_source_does_not_need_or_repeat_a_removal(self):
        source = {"services": {"mcp-http": {"configs": [], "volumes": [{"source": "protected", "target": "/run/rateldesk-mcp", "read_only": True}]}}}
        self.assertEqual(source, launcher.migrated_compose_source(source, source, {}))

    def test_parent_volume_requires_read_only_directory_shape_inspection_before_safe_coexistence(self):
        model = {"name": "fixture", "services": {"mcp-http": {"volumes": [{"type": "volume", "source": "run", "target": "/run"}]}}, "volumes": {"run": {"name": "fixture-run"}}}
        for missing, failed in ((True, False), (False, True), (False, False)):
            calls = []
            class DirectoryInspectionDocker:
                def run(self, arguments, **kwargs):
                    calls.append(arguments)
                    if arguments[0] == "volume":
                        return None if missing else b"existing-volume"
                    return None if failed else b""
            checks = launcher.inspect_mount_ancestors(DirectoryInspectionDocker(), model, "synthetic-image")
            self.assertEqual(len(checks), 0 if missing or failed else 1)
            self.assertFalse(any("create" in call for call in calls))
            if not missing:
                inspect = calls[-1]
                self.assertIn("type=volume,source=fixture-run,target=/ancestor,readonly,volume-nocopy", inspect)
                self.assertIn("/ancestor/rateldesk-mcp", inspect)
                self.assertIn("10001:10001", inspect)
                self.assertNotIn("mkdir", " ".join(inspect))

    def test_real_directory_shape_shell_rejects_regular_and_symlink_intermediate_components(self):
        root = self.directory / "ancestor"
        root.mkdir()
        regular = root / "regular"
        regular.write_text("synthetic unrelated state")
        link = root / "link"
        link.symlink_to(root, target_is_directory=True)
        for child, succeeds in ((root / "absent", True), (regular, False), (link, False)):
            result = subprocess.run(["/bin/sh", "-ec", launcher.ANCESTOR_INSPECTION_SCRIPT, "check",
                str(root), str(child), str(child / "descendant")], capture_output=True, timeout=5)
            self.assertEqual(result.returncode == 0, succeeds)
        self.assertEqual(regular.read_text(), "synthetic unrelated state")
        self.assertEqual(len(list(root.iterdir())), 2)

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

    def test_docker_proxy_alias_pairs_do_not_mask_configuration_duplicate_checks(self):
        environment = ["HTTP_PROXY=http://proxy.example.test", "http_proxy=http://proxy.example.test",
                       "NO_PROXY=api,localhost", "no_proxy=api,localhost", "Helpdesk__Mcp__Instance=fixture"]
        self.assertEqual(launcher.relevant_environment(environment), ["Helpdesk__Mcp__Instance=fixture", "NO_PROXY=api,localhost"])
        with self.assertRaisesRegex(launcher.Prerequisite, "environment_duplicate"):
            launcher.environment_map(environment + ["helpdesk:Mcp:Instance=another"])

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

    def test_new_verify_invocation_reuses_private_matching_owner_checkpoint(self):
        output = launcher.private_directory(self.directory / ".rateldesk-http-mcp-fixture")
        launcher.write_private(output / "launcher-state.json", {"project": "fixture", "manager": "release-compose"})
        metadata = {"Config": {"Image": "fixture", "Labels": {"com.docker.compose.project": "fixture",
            "com.docker.compose.service": "mcp-http", "com.docker.compose.project.config_files": str(self.source),
            "com.docker.compose.project.working_dir": str(self.directory)}}}
        args = launcher.parser().parse_args(["--container", "fixture", "--verify"])
        with patch("sys.stdin.isatty", return_value=False):
            resumed = launcher.Deployment(args, DiscoveryDocker(metadata))
        self.assertEqual(resumed.manager, "release-compose")
        launcher.write_private(output / "launcher-state.json", {"project": "another-project", "manager": "release-compose"})
        with patch("sys.stdin.isatty", return_value=False), self.assertRaisesRegex(launcher.Prerequisite, "deployment_manager_unknown"):
            launcher.Deployment(args, DiscoveryDocker(metadata))

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

    def test_unreadable_interpolation_env_has_distinct_safe_diagnosis(self):
        self.args.env_file = [str(self.directory / "missing.env")]
        with self.assertRaisesRegex(launcher.Prerequisite, "compose_env_file_unreadable"):
            self.deployment()
        self.assertFalse((self.directory / ".rateldesk-http-mcp-fixture").exists())

    def test_untracked_compose_dependencies_fail_with_complete_export_action(self):
        for text in ('include:\n  - child.yml\n', '{"services":{"api":{"extends":{"file":"child.yml","service":"api"}}}}',
                     'services: {api: {extends: {file: child.yml, service: api}}}'):
            self.source.write_text(text)
            with self.assertRaisesRegex(launcher.Prerequisite, "compose_dependency_unsupported") as failure:
                launcher.check_compose_dependencies(self.source)
            self.assertIn("--no-interpolate --no-env-resolution", str(failure.exception))

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

    def test_unreachable_current_administrator_check_prevents_release_apply(self):
        self.args.manager = "release-compose"
        deployment = self.deployment()
        deployment.image, deployment.storage = "fixture", {}
        deployment.proposal = {"x-rateldesk-source-files": [launcher.file_record(self.source)], "x-rateldesk-storage": {}}
        launcher.write_private(deployment.output / "effective-deployment.json", deployment.proposal)
        launcher.write_private(deployment.output / "deployment.json", {"services": {}})
        state = {"sources": deployment.proposal["x-rateldesk-source-files"], "exportHashes": {}, "storage": {},
                 "deploymentSha256": launcher.hashlib.sha256((deployment.output / "deployment.json").read_bytes()).hexdigest(),
                 "sourceHash": launcher.hashlib.sha256((deployment.output / "effective-deployment.json").read_bytes()).hexdigest(),
                 "phase": "saved-apply-pending"}
        launcher.write_private(deployment.output / "launcher-state.json", state)
        calls = []
        class AuthorityUnavailableDocker:
            def run(self, arguments, **kwargs):
                calls.append(arguments)
                if "approval-check" in arguments:
                    raise launcher.Prerequisite("docker_operation: unavailable")
                raise AssertionError("An unapproved Docker operation was attempted")
        deployment.docker = AuthorityUnavailableDocker()
        with patch.object(deployment, "prepare_storage"), patch.object(launcher, "stage_snapshot"), self.assertRaisesRegex(launcher.Prerequisite, "approval_revalidation"):
            launcher.apply_saved(deployment)
        self.assertTrue(any("approval-check" in arguments for arguments in calls))
        self.assertFalse(any("up" in arguments for arguments in calls))
        self.assertEqual(launcher.read_private_json(deployment.output / "launcher-state.json")["phase"], "saved-apply-pending")

    def test_runtime_recreated_after_successful_protocol_checks_cannot_complete_verification(self):
        self.args.verify = True
        deployment = self.deployment()
        deployment.image = "fixture"
        deployment.storage = {
            purpose: {"type": "volume", "source": purpose + "-fixture", "target": target,
                      "readOnly": False, "private": True, "uid": 10001}
            for purpose, target in (("host", "/output"), ("client", "/var/lib/rateldesk/http-client"))}
        identifier = "sha256:" + "a" * 64
        deployment.proposal = {"services": {name: {"image": "fixture", "environment": {}} for name in launcher.SERVICE_NAMES},
            "x-rateldesk-source-files": [launcher.file_record(self.source)], "x-rateldesk-storage": deployment.storage,
            "x-rateldesk-image-ids": {name: identifier for name in launcher.SERVICE_NAMES}}
        launcher.write_private(deployment.output / "effective-deployment.json", deployment.proposal)
        launcher.write_private(deployment.output / "runtime-environment.json", {"services": {}})
        launcher.write_private(deployment.output / "deployment.json", {"services": {}})
        state = {"sources": deployment.proposal["x-rateldesk-source-files"], "exportHashes": {}, "storage": deployment.storage,
                 "deploymentSha256": launcher.hashlib.sha256((deployment.output / "deployment.json").read_bytes()).hexdigest(),
                 "sourceHash": launcher.hashlib.sha256((deployment.output / "effective-deployment.json").read_bytes()).hexdigest(),
                 "phase": "end-to-end-verified"}
        launcher.write_private(deployment.output / "launcher-state.json", state)
        staged = {}
        calls = []
        class RecreatedRuntimeDocker:
            def run(self, arguments, **kwargs):
                calls.append(arguments)
                if arguments[0] == "ps":
                    service = next(item.rsplit("=", 1)[1] for item in arguments if item.startswith("label=com.docker.compose.service="))
                    return (json.dumps(service) + "\n").encode()
                if arguments[0] == "cp":
                    return None
                if arguments[0] == "rm":
                    return b"removed"
                if "observation-check" in arguments:
                    current = staged["runtime-environment.json"]
                    self.assert_current(current)
                    raise launcher.Prerequisite("HOST_RUNTIME_MISMATCH")
                raise AssertionError(arguments)
            @staticmethod
            def assert_current(current):
                assert current["services"]["mcp-http"]["environment"]["Helpdesk__Mcp__Instance"] == "unreviewed"
                assert current["x-rateldesk-observed-at-utc"].endswith("Z")
            def inspect(self, service):
                mounts = [] if service != "mcp-http" else [{"Type": "volume", "Name": store["source"],
                    "Destination": store["target"], "RW": True} for store in deployment.storage.values()]
                return {"Image": identifier, "Config": {"Image": "fixture", "WorkingDir": "/app", "Env": ["Helpdesk__Mcp__Instance=unreviewed"] if service == "mcp-http" else [],
                    "Labels": {"com.docker.compose.oneoff": "false"}, "Cmd": [], "Entrypoint": ["dotnet"]}, "Mounts": mounts}
        deployment.docker = RecreatedRuntimeDocker()
        def stage(current, name):
            staged[name] = launcher.read_private_json(current.output / name)
        with patch.object(deployment, "prepare_storage"), patch.object(launcher, "stage_snapshot", side_effect=stage), \
             patch.object(launcher.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)), \
             self.assertRaisesRegex(launcher.Prerequisite, "runtime_revalidation"):
            launcher.run_setup(deployment, self.args)
        self.assertTrue(any("observation-check" in arguments for arguments in calls))
        self.assertEqual(launcher.read_private_json(deployment.output / "launcher-state.json")["phase"], "applied-verify-pending")

    def test_new_verify_demotes_previous_success_before_initial_docker_observation(self):
        deployment = self.deployment()
        launcher.write_private(deployment.output / "launcher-state.json", {"phase": "end-to-end-verified"})
        def initial_observation_failure(**kwargs):
            self.assertEqual(launcher.read_private_json(deployment.output / "launcher-state.json")["phase"], "applied-verify-pending")
            raise launcher.Prerequisite("runtime_image_mismatch")
        with patch.object(launcher.Docker, "preflight"), patch.object(launcher, "Deployment", return_value=deployment), \
             patch.object(deployment, "snapshot", side_effect=initial_observation_failure), \
             self.assertRaisesRegex(launcher.Prerequisite, "runtime_image_mismatch"):
            launcher.main(["--verify"])
        self.assertEqual(launcher.read_private_json(deployment.output / "launcher-state.json")["phase"], "applied-verify-pending")

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

    def test_exact_complete_import_still_rejects_unrelated_effective_interpolation_drift(self):
        self.source.write_text(json.dumps({"services": {"api": {"image": "fixture",
            "environment": {"ConnectionStrings__Helpdesk": "${RATELDESK_FIXTURE_DB}"}},
            "untouched": {"image": "fixture", "command": ["sleep", "3600"]}}}))
        deployment = self.deployment()
        deployment.docker = launcher.Docker()
        records = [launcher.file_record(self.source)]
        with patch.dict(os.environ, {"RATELDESK_FIXTURE_DB": "reviewed-private-database"}):
            rendered = deployment.docker.json(deployment.compose + ["config", "--format", "json"])
        state = {"sources": [{"path": str(self.source), "sha256": "original-source-before-complete-import"}],
            "resolvedDeploymentSha256": launcher.resolved_deployment_hash(rendered)}
        deployment.proposal = {**rendered, "x-rateldesk-source-files": records}
        launcher.check_effective_context(deployment, state)
        with patch.dict(os.environ, {"RATELDESK_FIXTURE_DB": "changed-private-database"}):
            changed = deployment.docker.json(deployment.compose + ["config", "--format", "json"])
        deployment.proposal = {**changed, "x-rateldesk-source-files": records}
        self.assertEqual(records, [launcher.file_record(self.source)], "The complete source bytes did not change")
        with self.assertRaisesRegex(launcher.Prerequisite, "approved_context_changed"):
            launcher.check_effective_context(deployment, state)

    def test_exact_complete_import_rejects_changed_selected_named_volume_secret_bytes(self):
        deployment = self.deployment()
        descriptor = {"type": "volume", "source": "fixture-existing-identity", "target": "/run/secrets",
            "path": "/run/secrets/id", "sha256": "a" * 64}
        document = {"services": {"mcp-http": {"image": "fixture", "environment": {
            "Helpdesk__Mcp__Outbound__IntegrationCredentialFile": "/run/secrets/id"}}}}
        state = {"sources": [{"path": str(self.source), "sha256": "source-before-exact-import"}],
            "resolvedDeploymentSha256": launcher.resolved_deployment_hash(document), "secretMounts": [descriptor]}
        deployment.proposal = {**document, "x-rateldesk-source-files": [launcher.file_record(self.source)],
            "x-rateldesk-secret-mounts": [descriptor.copy()]}
        launcher.check_effective_context(deployment, state)
        deployment.proposal["x-rateldesk-secret-mounts"][0]["sha256"] = "b" * 64
        self.assertEqual(launcher.resolved_deployment_hash(deployment.proposal), state["resolvedDeploymentSha256"])
        with self.assertRaisesRegex(launcher.Prerequisite, "approved_secret_changed"):
            launcher.check_effective_context(deployment, state)

    def test_import_secret_binding_tracks_final_selected_identity_and_permits_approved_gateway_clear(self):
        descriptor = {"type": "volume", "source": "fixture-existing-identity", "target": "/run/secrets",
            "path": "/run/secrets/app", "sha256": "a" * 64}
        outbound = {"credentialMode": "authentik", "authentikAppPasswordFile": descriptor["path"]}
        proposal = {"x-rateldesk-secret-mounts": [descriptor], "x-rateldesk-image-environment": {"mcp-http": []},
            "x-rateldesk-image-commands": {"mcp-http": []}, "x-rateldesk-protected-configuration": {"mcp-http": [
                {"path": "/run/original.json", "content": json.dumps(outbound)}]}}
        complete = {"services": {"mcp-http": {"environment": {
            "Helpdesk__Mcp__ConfigurationPath": "/run/rateldesk-mcp/config.json"}}}}
        self.assertEqual(launcher.approved_import_secret_mounts(proposal, complete, json.dumps(outbound)), [descriptor])
        complete["services"]["mcp-http"]["environment"].update({"Helpdesk__Mcp__Outbound__CredentialMode": "gateway",
            "Helpdesk__Mcp__Outbound__AuthentikAppPasswordFile": ""})
        expected = launcher.approved_import_secret_mounts(proposal, complete, '{"credentialMode":"gateway"}')
        self.assertEqual(expected, [], "An approved gateway transition no longer selects the original external identity")
        deployment = self.deployment()
        deployment.proposal = {**complete, "x-rateldesk-source-files": [launcher.file_record(self.source)], "x-rateldesk-secret-mounts": []}
        state = {"sources": [], "resolvedDeploymentSha256": launcher.resolved_deployment_hash(complete), "secretMounts": expected}
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
        class EmptyDocker(DiscoveryDocker):
            def run(self, arguments, **kwargs):
                return b""
        with patch.object(launcher, "__file__", str(self.directory / "setup-http-mcp.py")):
            deployment = launcher.Deployment(args, EmptyDocker({}))
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

    def test_command_selected_configuration_path_overrides_lower_providers_and_explicit_clear(self):
        deployment = self.deployment()
        deployment.provider_source_records = {}
        providers = {"/app/appsettings.json": b'{"Helpdesk":{"Mcp":{"ConfigurationPath":"/run/lower.json"}}}',
                     "/run/cli.json": b'{"ApiBaseUrl":"http://private-api:8222/","CredentialMode":"gateway"}',
                     "/run/legacy.json": b'{"ApiBaseUrl":"http://preserved-api:8222/","CredentialMode":"gateway"}'}
        class CommandDocker:
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
        deployment.docker = CommandDocker()
        service = {"environment": {"Helpdesk__Mcp__ConfigurationPath": "/run/environment.json"},
                   "command": ["--Helpdesk:Mcp:ConfigurationPath", "/run/cli.json"]}
        result = deployment.configuration_providers("mcp-http", service, "fixture", [],
            image_command=["--Helpdesk:Mcp:ConfigurationPath=/run/image.json"])
        self.assertEqual([provider["path"] for provider in result], ["/app/appsettings.json", "/run/cli.json"])
        service["command"] = ["--Helpdesk:Mcp:ConfigurationPath="]
        service["environment"]["RATELDESK_MCP_CONFIG"] = "/run/legacy.json"
        cleared = deployment.configuration_providers("mcp-http", service, "fixture", [])
        self.assertEqual([provider["path"] for provider in cleared], ["/app/appsettings.json", "/run/legacy.json"])

    def test_alternate_application_and_content_roots_fail_before_provider_reads(self):
        alternate = self.directory / "alternate-appsettings.json"
        alternate.write_text('{"Helpdesk":{"Mcp":{"AllowedOrigins":["https://reviewed.example.test","https://unreviewed.example.test"]}}}')
        mount = {"type": "bind", "source": str(alternate), "target": "/alternate/appsettings.json"}
        cases = [
            ({"command": ["--contentRoot=/alternate"]}, [], [], "/app"),
            ({"command": ["--ContentROOT", "/alternate"]}, [], [], "/app"),
            ({}, [], ["--contentRoot=/alternate"], "/app"),
            ({"environment": {"DOTNET_CONTENTROOT": "/alternate"}}, [], [], "/app"),
            ({"environment": {"ASPNETCORE_CONTENTROOT": "/alternate"}}, [], [], "/app"),
            ({"environment": {"contentRoot": "/alternate"}}, [], [], "/app"),
            ({}, ["DOTNET_CONTENTROOT=/alternate"], [], "/app"),
            ({"working_dir": "/alternate"}, [], [], "/app"),
            ({}, [], [], "/alternate"),
            ({}, [], [], ""),
        ]
        class NoProviderReads:
            def run(self, arguments, **kwargs):
                raise AssertionError("Alternate roots must be rejected before Docker create/cp")
        for service, environment, command, working_directory in cases:
            with self.subTest(service=service, image_environment=environment, image_command=command, image_working_directory=working_directory):
                deployment = self.deployment()
                deployment.docker = NoProviderReads()
                service = {"environment": {}, "volumes": [mount], **service}
                sources = []
                with self.assertRaisesRegex(launcher.Prerequisite, "content_root_unsupported"):
                    deployment.configuration_providers("mcp-http", service, "fixture", sources,
                        image_environment=environment, image_command=command, image_working_directory=working_directory)
                self.assertEqual(sources, [])

    def test_service_env_file_change_during_full_render_is_rejected_before_approval(self):
        environment = self.directory / "service.env"
        environment.write_text("SOURCE=before\n")
        self.args.image = "fixture"
        deployment = self.deployment()
        services = {name: {"image": "fixture", "environment": {}} for name in launcher.SERVICE_NAMES}
        services["mcp-http"]["env_file"] = [{"path": str(environment), "required": True}]
        class RacingDocker:
            def json(self, arguments):
                if arguments[0] == "compose":
                    if any(".optional-source-paths.json" in argument for argument in arguments):
                        probe = json.loads((deployment.output / ".optional-source-paths.json").read_text())
                        return probe
                    if "--no-env-resolution" not in arguments:
                        environment.write_text("SOURCE=after\n")
                    return json.loads(json.dumps({"services": services}))
                raise AssertionError(arguments)
            def run(self, arguments, **kwargs):
                if arguments[0] == "image":
                    return json.dumps({"Id": "sha256:" + "a" * 64, "Config": {"WorkingDir": "/app", "Env": [], "Entrypoint": ["dotnet"], "Cmd": []}}).encode()
                if arguments[0] == "create":
                    return b"a" * 64
                if arguments[0] == "cp":
                    return None
                if arguments[0] == "rm":
                    return b"removed"
                raise AssertionError(arguments)
        deployment.docker = RacingDocker()
        with self.assertRaisesRegex(launcher.Prerequisite, "sources_changed"):
            deployment.snapshot()
        self.assertFalse((deployment.output / "host-approval.json").exists())

    def test_optional_provider_presence_is_source_bound_through_actual_compose(self):
        optional = self.directory / "optional.env"
        interpolation = self.directory / "interpolation.env"
        interpolation.write_text("OPTIONAL_ROOT=" + str(self.directory) + "\n")
        services = {name: {"image": "fixture", "environment": {}} for name in launcher.SERVICE_NAMES}
        services["mcp-http"]["env_file"] = [{"path": "${OPTIONAL_ROOT}/optional.env", "required": False}]
        self.source.write_text(json.dumps({"services": services}))
        self.args.image = "fixture"
        self.args.env_file = [str(interpolation)]
        deployment = self.deployment()
        class ComposeWithImageFixture(launcher.Docker):
            def run(self, arguments, **kwargs):
                if arguments[0] == "compose":
                    return super().run(arguments, **kwargs)
                if arguments[0] == "image":
                    return json.dumps({"Id": "sha256:" + "a" * 64, "Config": {"WorkingDir": "/app", "Env": [], "Cmd": [], "Entrypoint": ["dotnet"]}}).encode()
                if arguments[0] == "create":
                    return b"a" * 64
                if arguments[0] == "cp":
                    return None
                if arguments[0] == "rm":
                    return b"removed"
                raise AssertionError(arguments)
        deployment.docker = ComposeWithImageFixture()
        proposal = deployment.snapshot()
        self.assertTrue(any(item["path"] == str(optional) and item.get("present") is False for item in proposal["x-rateldesk-source-files"]))
        retained = launcher.read_private_json(deployment.output / ".compose-source.json")
        self.assertEqual(retained["services"]["mcp-http"]["env_file"][0]["path"], "${OPTIONAL_ROOT}/optional.env")
        deployment.check_sources()
        optional.write_text("Helpdesk__Mcp__Instance=unreviewed\n")
        actual = deployment.docker.json(deployment.compose + ["config", "--format", "json"])
        self.assertEqual(actual["services"]["mcp-http"]["environment"]["Helpdesk__Mcp__Instance"], "unreviewed")
        with self.assertRaisesRegex(launcher.Prerequisite, "sources_changed"):
            deployment.check_sources()

    def test_forwarded_options_are_literal_arguments(self):
        args = launcher.parser().parse_args(["--", "--web-url", "https://desk.example.test", "--instance", "stage-$(touch ignored)"])
        self.assertEqual(args.wizard_options[1:], ["--web-url", "https://desk.example.test", "--instance", "stage-$(touch ignored)"])
        self.assertFalse((self.directory / "ignored").exists())

    def test_docker_selection_is_explicit_in_every_process_and_quoted_resume(self):
        selection = {"DOCKER_CONFIG": str(self.directory / "private config $(touch ignored)"),
            "DOCKER_CONTEXT": "fixture", "DOCKER_HOST": "tcp://127.0.0.1:2376", "DOCKER_TLS_VERIFY": "1",
            "DOCKER_CERT_PATH": str(self.directory / "public cert path"), "DOCKER_API_VERSION": "1.47"}
        with patch.dict(os.environ, selection, clear=True):
            docker = launcher.Docker()
        deployment = self.deployment()
        deployment.docker = docker
        with patch.dict(os.environ, {"DOCKER_CONFIG": "changed", "DOCKER_CONTEXT": "wrong"}, clear=True):
            self.assertEqual({key: docker.process_environment()[key] for key in selection}, selection)
            for mode in ("--apply", "--verify"):
                command = shlex.split(launcher.resume_command(deployment, mode))
                self.assertEqual(command[0], "env")
                for key, value in selection.items():
                    self.assertIn(key + "=" + value, command)
                self.assertEqual(command[-1], mode)
        self.assertFalse((self.directory / "ignored").exists())

    def test_fresh_resume_rejects_different_docker_selection_or_daemon(self):
        with patch.dict(os.environ, {"DOCKER_CONTEXT": "approved"}, clear=True):
            docker = launcher.Docker()
        docker.daemon_identity = "original"
        docker.check_selection({"dockerSelection": {"DOCKER_CONTEXT": "approved"}, "dockerDaemonIdentity": "original"})
        with self.assertRaisesRegex(launcher.Prerequisite, "docker_selection_changed"):
            docker.check_selection({"dockerSelection": {"DOCKER_CONTEXT": "another"}})
        with self.assertRaisesRegex(launcher.Prerequisite, "docker_daemon_changed"):
            docker.check_selection({"dockerSelection": {"DOCKER_CONTEXT": "approved"}, "dockerDaemonIdentity": "another"})

    def test_client_tooling_keeps_selection_and_redacts_docker_start_failure(self):
        deployment = self.deployment()
        deployment.image, deployment.storage, self.args.target = "fixture", {}, "http-client"
        with patch.dict(os.environ, {"DOCKER_CONFIG": str(self.directory / "chosen Docker config")}, clear=True):
            deployment.docker = launcher.Docker()
        selection = deployment.docker.selection
        def failed(command, **kwargs):
            self.assertEqual({key: kwargs["env"][key] for key in selection}, selection)
            if command[1] == "run":
                kwargs["stderr"].write(b"private-proxy-credential-never-print")
                return subprocess.CompletedProcess(command, 125)
            self.assertEqual(command[1], "rm")
            return subprocess.CompletedProcess(command, 0)
        with patch.object(deployment, "prepare_storage"), patch.object(deployment, "check_sources"), \
                patch.object(launcher, "stage_snapshot"), patch.object(launcher.subprocess, "run", side_effect=failed), \
                self.assertRaisesRegex(launcher.Prerequisite, "docker_tooling_unavailable") as failure:
            launcher.run_setup(deployment, self.args)
        self.assertNotIn("private-proxy", str(failure.exception))

    def test_selected_legacy_config_file_is_captured_without_image_fallback(self):
        deployment = self.deployment()
        deployment.provider_source_records = {}
        protected = self.directory / "legacy protected.json"
        content = '{"ApiBaseUrl":"http://private-api:8222/","CredentialMode":"authentik","AuthentikAppPasswordFile":"/run/secrets/downstream"}'
        protected.write_text(content)
        deployment.rendered = {"configs": {"outbound": {"file": str(protected)}}}
        copied = []
        class ProviderDocker:
            def run(self, arguments, **kwargs):
                if arguments[0] == "cp":
                    copied.append(arguments[1])
                    return None
                raise AssertionError(arguments)
        deployment.docker = ProviderDocker()
        service = {"environment": {"Helpdesk__Mcp__ConfigurationPath": "/run/rateldesk-mcp/config.json"},
            "configs": [{"source": "outbound", "target": "//run//rateldesk-mcp/./config.json"}]}
        paths = []
        result = deployment.configuration_providers("mcp-http", service, "fixture", paths, container="running")
        self.assertEqual(result[-1]["content"], content)
        self.assertEqual(paths, [protected])
        self.assertNotIn("running:/run/rateldesk-mcp/config.json", copied)
        protected.unlink()
        with self.assertRaisesRegex(launcher.Prerequisite, "protected_provider_unreadable"):
            deployment.configuration_providers("mcp-http", service, "fixture", [], container="running")
        deployment.rendered = {"configs": {"outbound": {"external": True}}}
        with self.assertRaisesRegex(launcher.Prerequisite, "protected_provider_unreadable"):
            deployment.configuration_providers("mcp-http", service, "fixture", [], container="running")

    def test_safe_docker_errors_distinguish_source_env_permission_daemon_and_timeout(self):
        secret = "private-source-value-never-print"
        cases = [(["compose", "version", "--short"], "docker: compose is not a docker command", "compose_plugin_missing"),
            (["compose", "config"], "invalid compose syntax " + secret, "compose_source_invalid"),
            (["compose", "config"], "env file missing " + secret, "compose_env_file_unreadable"),
            (["compose", "config"], "permission denied " + secret, "compose_source_permission"),
            (["info"], "Cannot connect to the Docker daemon " + secret, "docker_daemon_unavailable")]
        for arguments, stderr, code in cases:
            def failed(command, **kwargs):
                kwargs["stderr"].write(stderr.encode())
                return subprocess.CompletedProcess(command, 1)
            with self.subTest(code=code), patch.object(launcher.subprocess, "run", side_effect=failed), \
                    self.assertRaisesRegex(launcher.Prerequisite, code) as failure:
                launcher.Docker().run(arguments)
            self.assertNotIn(secret, str(failure.exception))
        with patch.object(launcher.subprocess, "run", side_effect=subprocess.TimeoutExpired("docker", 45)), \
                self.assertRaisesRegex(launcher.Prerequisite, "docker_timeout"):
            launcher.Docker().run(["info"])
        with patch.object(launcher.subprocess, "run", side_effect=FileNotFoundError()), \
                self.assertRaisesRegex(launcher.Prerequisite, "docker_cli_missing"):
            launcher.Docker().run(["info"])

    def test_missing_capabilities_stop_before_source_storage_wizard_and_consent(self):
        for version, missing in (("2.26.1-4", "--no-env-resolution"), ("2.39.4", "--no-path-resolution"),
                                 ("untrusted version\nprivate-value", "--no-env-resolution")):
            calls = []
            def safe_probe(docker, arguments, **kwargs):
                calls.append(arguments)
                if arguments == ["compose", "version", "--short"]:
                    return version.encode()
                if arguments == ["compose", "config", "--help"]:
                    return " ".join(flag for flag in launcher.COMPOSE_FLAGS if flag != missing).encode()
                raise AssertionError("Source, daemon or service operation before capability diagnosis")
            with self.subTest(version=version), patch.object(launcher.Docker, "run", safe_probe), \
                    patch.object(launcher, "Deployment") as deployment, \
                    self.assertRaisesRegex(launcher.Prerequisite, "compose_feature_unsupported") as failure:
                launcher.main(["--compose-file", "/not-read", "--project", "fixture"])
            deployment.assert_not_called()
            self.assertIn(missing, str(failure.exception))
            self.assertNotIn("private-value", str(failure.exception))
            self.assertFalse((self.directory / ".rateldesk-http-mcp-fixture").exists())
            self.assertEqual(len(calls), 2)

    def test_storage_descriptors_cannot_smuggle_docker_mount_options(self):
        with self.assertRaisesRegex(launcher.Prerequisite, "storage_source"):
            launcher.mount_argument({"type": "volume", "source": "fixture,readonly", "target": "/output"})

    def test_malformed_display_metadata_never_prints_control_sequences(self):
        with self.assertRaisesRegex(launcher.Prerequisite, "deployment_metadata"):
            launcher.safe_label("instance\nsecret")


class ComposeContract(unittest.TestCase):
    def test_selected_real_plugin_passes_synthetic_source_and_daemon_capability_probes(self):
        docker = launcher.Docker()
        docker.preflight()
        self.assertRegex(docker.daemon_identity, r"^[a-f0-9]{64}$")

    def test_version_text_does_not_override_working_actual_capabilities(self):
        class VersionDocker(launcher.Docker):
            def run(self, arguments, **kwargs):
                if arguments == ["compose", "version", "--short"]:
                    return b"vendor build with unparseable version\n"
                return super().run(arguments, **kwargs)
        docker = VersionDocker()
        docker.preflight()
        self.assertEqual(docker.compose_version, "unparseable")

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

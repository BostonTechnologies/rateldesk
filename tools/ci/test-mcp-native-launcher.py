#!/usr/bin/env python3
"""Native adapter/source-fence contracts without running a Docker deployment."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shlex
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("mcp_launcher", ROOT / "tools/mcp/setup-http-mcp.py")
launcher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(launcher)


class NativeLauncherContracts(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="native launcher contract ")
        self.directory = Path(self.temporary.name)
        self.output = launcher.private_directory(self.directory / "private checkpoints")
        launcher.write_private(self.output / "runtime-environment.json", {"x-rateldesk-runtime-inventory": {}})
        self.payload = self.directory / "native payload"
        self.payload.mkdir()
        self.executable = self.payload / "rateldesk-mcp"
        self.calls = self.directory / "native calls"
        self.executable.write_text("#!/bin/sh\nprintf '%s\\n' \"$*\" >> " + shlex.quote(str(self.calls)) + "\nexit 0\n")
        self.executable.chmod(0o700)
        self.source = self.directory / "operator compose.yml"
        self.source.write_text("# operator formatting\nservices:\n  mcp-http: {image: synthetic:never-run}\n")
        self.args = launcher.parser().parse_args(["--native-executable", str(self.executable), "--preflight"])
        docker = SimpleNamespace(selection={"DOCKER_CONTEXT": "synthetic"}, daemon_identity="same-daemon", compose_version="2.39.4",
            process_environment=lambda: os.environ.copy(), check_selection=Mock(), bind_daemon=Mock(),
            run=Mock(side_effect=AssertionError("Native adapter must not create, run, mount or restart Docker resources")))
        self.deployment = SimpleNamespace(args=self.args, docker=docker, output=self.output, manager="external-compose",
            intent="verify-existing", legacy=False, compose=["compose", "-f", str(self.source)], sources=[self.source],
            env_files=[], directory=self.directory, project="fixture",
            proposal={"x-rateldesk-source-files": [launcher.file_record(self.source)]},
            check_sources=Mock(), observe_runtime=Mock(), validate_resume_sources=Mock())

    def tearDown(self):
        self.temporary.cleanup()

    def approved_checkpoint(self, *, candidates=None, extra_exports=(), intent=0):
        """The real receipt schema binds launcher state to approved C# files."""
        self.deployment.intent = {0: "verify-existing", 1: "prepare-changes"}[intent]
        _, identity = launcher.native_executable(self.args)
        inventory = {"mcp-http": {"containerId": "synthetic-container", "startedAt": "2026-10-17T00:00:00Z",
            "restartCount": 0, "state": "running", "healthStatus": "absent"}}
        launcher.write_private(self.output / "runtime-environment.json", {"x-rateldesk-runtime-inventory": inventory})
        frozen = {"services": {"mcp-http": {"image": "synthetic:never-run"}},
            "x-rateldesk-source-files": [launcher.file_record(self.source)],
            "x-rateldesk-project-name": self.deployment.project,
            "x-rateldesk-deployment-manager": self.deployment.manager,
            "x-rateldesk-project-directory": str(self.deployment.directory),
            "x-rateldesk-compose-files": [str(path) for path in self.deployment.sources],
            "x-rateldesk-interpolation-files": [str(path) for path in self.deployment.env_files],
            "x-rateldesk-docker-selection": self.deployment.docker.selection,
            "x-rateldesk-docker-daemon-identity": self.deployment.docker.daemon_identity,
            "x-rateldesk-compose-version": self.deployment.docker.compose_version,
            "x-rateldesk-native-payload-identity": identity,
            "x-rateldesk-runtime-baseline-sha256": hashlib.sha256(json.dumps(launcher.stable_runtime_inventory(inventory),
                sort_keys=True, separators=(",", ":")).encode()).hexdigest()}
        approved = json.loads(json.dumps(frozen))
        approved["x-rateldesk-source-candidates"] = candidates or []
        if candidates:
            approved["services"]["mcp-http"]["environment"] = {"SYNTHETIC": "reviewed change"}
        frozen_path, approved_path = self.output / "approved-effective-deployment.json", self.output / "approved-proposal.json"
        launcher.write_private(frozen_path, frozen)
        launcher.write_private(approved_path, approved)
        exports = [frozen_path, approved_path, *extra_exports]
        receipt = {"approved": True, "sourceHash": hashlib.sha256(frozen_path.read_bytes()).hexdigest(),
            "proposalHash": hashlib.sha256(approved_path.read_bytes()).hexdigest(), "intent": intent,
            "approvedBy": "synthetic-administrator", "approvedAtUtc": "2026-10-17T00:00:00Z",
            "exportHashes": {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in exports}}
        launcher.write_private(self.output / "host-approval.json", receipt)
        self.deployment.proposal = frozen
        return launcher.save_native_checkpoint(self.deployment)

    def test_native_client_main_does_not_construct_docker_or_read_compose_sources(self):
        with patch.object(launcher, "Docker", side_effect=AssertionError("Client must not require Docker")):
            result = launcher.main(["--target", "http-client", "--native-executable", str(self.executable),
                "--", "--profile", "private profile", "--url", "https://desk.example.test/mcp/"])
        self.assertEqual(result, 0)
        self.assertEqual(self.calls.read_text().strip(), "setup --target http-client --profile private profile --url https://desk.example.test/mcp/")
        self.assertFalse((self.output / "launcher-state.json").exists())

    def test_native_host_preflight_uses_local_checkpoint_and_no_tooling_container(self):
        original = self.source.read_bytes()
        self.assertEqual(launcher.run_setup(self.deployment, self.args), 0)
        self.assertEqual(self.source.read_bytes(), original)
        self.assertEqual(self.calls.read_text().strip(), "host preflight --export-directory " + str(self.output))
        self.deployment.docker.run.assert_not_called()
        context = launcher.read_private_json(self.output / "source-review-context.json")
        self.assertEqual(context["sources"], [str(self.source)])
        self.assertEqual(context["dockerSelection"], self.deployment.docker.selection)
        self.assertEqual(context["daemonIdentity"], "same-daemon")
        self.assertTrue(context["ownerComposeRequired"])
        self.assertFalse((self.output / "launcher-state.json").exists())

    def test_cancelled_adoption_does_not_invent_an_approved_generation(self):
        self.args.preflight = False
        self.assertEqual(launcher.run_setup(self.deployment, self.args), 0)
        self.assertFalse((self.output / "launcher-state.json").exists())
        self.deployment.docker.run.assert_not_called()

    def test_fresh_adoption_verify_checks_native_observation_before_recording_success(self):
        self.args.preflight, self.args.verify = False, True
        self.approved_checkpoint()
        original = self.source.read_bytes()
        self.assertEqual(launcher.run_setup(self.deployment, self.args), 0)
        self.assertEqual(self.source.read_bytes(), original)
        self.assertEqual(self.calls.read_text().splitlines(), ["host verify --export-directory " + str(self.output),
            "host observation-check --export-directory " + str(self.output)])
        self.deployment.observe_runtime.assert_called_once_with(self.deployment.proposal)
        self.deployment.docker.run.assert_not_called()
        self.assertEqual(launcher.read_private_json(self.output / "launcher-state.json")["phase"], "existing-verified-unchanged")

    def test_changed_native_assembly_is_rejected_before_execution_after_review(self):
        dependency = self.payload / "Helpdesk.Setup.dll"
        dependency.write_bytes(b"reviewed managed payload")
        _, identity = launcher.native_executable(self.args)
        launcher.write_private(self.output / "launcher-state.json", {"nativeExecutableSha256": identity})
        self.args.verify = True
        dependency.write_bytes(b"different managed payload")
        with self.assertRaisesRegex(launcher.Prerequisite, "native_setup_changed"):
            launcher.native_command(self.deployment, ["host", "verify"], interactive=True)
        self.assertFalse(self.calls.exists())

    def test_adopted_baseline_cannot_enter_apply_or_restart_services(self):
        self.deployment.manager = "release-compose"
        launcher.write_private(self.output / "launcher-state.json", {"intent": "verify-existing"})
        with self.assertRaisesRegex(launcher.Prerequisite, "adoption_has_no_apply"):
            launcher.apply_native(self.deployment)
        self.deployment.docker.run.assert_not_called()
        self.assertFalse(self.calls.exists())

    def test_external_manager_cannot_be_applied_by_release_owner_helper(self):
        with self.assertRaisesRegex(launcher.Prerequisite, "manager_apply_required"):
            launcher.apply_native(self.deployment)
        self.deployment.docker.run.assert_not_called()
        self.assertFalse(self.calls.exists())

    def test_changed_private_candidate_and_checkpoint_are_rejected_without_hash_repair(self):
        candidate = self.output / "candidate.yml"
        candidate.write_bytes(b"reviewed source")
        checkpoint = self.output / "reviewed-settings.json"
        launcher.write_private(checkpoint, {"synthetic": "reviewed checkpoint"})
        state = self.approved_checkpoint(candidates=[{"source": str(self.source), "candidate": str(candidate),
            "originalSha256": launcher.file_record(self.source)["sha256"],
            "candidateSha256": hashlib.sha256(candidate.read_bytes()).hexdigest()}], extra_exports=[checkpoint], intent=1)
        self.args.apply = True  # original source is the trusted pre-apply model
        launcher.check_native_checkpoint(self.deployment, state)
        candidate.write_bytes(b"changed source")
        with self.assertRaisesRegex(launcher.Prerequisite, "approved_candidate_changed"):
            launcher.check_native_checkpoint(self.deployment, state)
        checkpoint.write_bytes(b"changed checkpoint")
        with self.assertRaisesRegex(launcher.Prerequisite, "approved_export_changed"):
            launcher.check_native_checkpoint(self.deployment, state)
        self.assertEqual(candidate.read_bytes(), b"changed source")
        self.assertEqual(checkpoint.read_bytes(), b"changed checkpoint")

    def test_launcher_state_cannot_broaden_approved_service_impact_or_replace_source_candidates(self):
        state = self.approved_checkpoint()
        for field, wrong in (("changedServices", ["database"]), ("sources", []),
                ("sourceCandidates", [{"source": str(self.source), "candidate": "/unreviewed/source"}]),
                ("dockerDaemonIdentity", "different-daemon"), ("intent", "prepare-changes")):
            changed = json.loads(json.dumps(state))
            changed[field] = wrong
            with self.subTest(field=field), self.assertRaisesRegex(launcher.Prerequisite, "approved_checkpoint_changed"):
                launcher.check_native_checkpoint(self.deployment, changed)
        self.assertEqual(launcher.read_private_json(self.output / "launcher-state.json"), state)

    def test_reviewed_external_owner_cannot_be_replaced_with_release_owner_on_resume(self):
        state = self.approved_checkpoint()
        self.deployment.manager = "release-compose"
        with self.assertRaisesRegex(launcher.Prerequisite, "approved_owner_changed"):
            launcher.check_native_checkpoint(self.deployment, state)
        self.deployment.docker.run.assert_not_called()

    def prepared_release_change(self):
        self.deployment.manager = "release-compose"
        self.args.preflight, self.args.apply = False, True
        candidate = self.output / "candidate.yml"
        candidate_data = b"# operator formatting\nservices:\n  mcp-http:\n    image: synthetic:never-run\n    environment:\n      SYNTHETIC: reviewed change\n"
        launcher.write_private(candidate, candidate_data)
        state = self.approved_checkpoint(candidates=[{"source": str(self.source), "candidate": str(candidate),
            "originalSha256": launcher.file_record(self.source)["sha256"],
            "candidateSha256": hashlib.sha256(candidate_data).hexdigest()}], intent=1)
        return candidate, candidate_data, state

    def test_candidate_or_staged_changes_during_authority_and_runtime_checks_never_reach_owner_apply(self):
        for boundary in ("authority-candidate", "runtime-candidate", "runtime-staged"):
            with self.subTest(boundary=boundary):
                candidate, _, _ = self.prepared_release_change()
                original = self.source.read_bytes()
                observations = [0]
                def approval(*arguments, **kwargs):
                    if boundary == "authority-candidate":
                        candidate.write_bytes(b"unreviewed candidate during authority check")
                    return 0
                def observe(*arguments):
                    observations[0] += 1
                    if observations[0] == 2:
                        staged = list(self.directory.glob(".rateldesk-reviewed-*"))
                        self.assertEqual(len(staged), 1)
                        self.assertEqual(staged[0].stat().st_mode & 0o777, 0o600)
                        if boundary == "runtime-candidate":
                            candidate.write_bytes(b"unreviewed candidate after staging")
                        elif boundary == "runtime-staged":
                            staged[0].write_bytes(b"unreviewed staged payload")
                self.deployment.observe_runtime.side_effect = observe
                code = "approved_staged_source_changed" if boundary == "runtime-staged" else "approved_candidate_changed"
                with patch.object(launcher, "native_command", side_effect=approval), self.assertRaisesRegex(launcher.Prerequisite, code):
                    launcher.apply_native(self.deployment)
                self.assertEqual(self.source.read_bytes(), original)
                self.assertEqual(list(self.directory.glob(".rateldesk-reviewed-*")), [])
                self.assertEqual(launcher.read_private_json(self.output / "launcher-state.json")["phase"], "prepared-apply-pending")
                self.deployment.docker.run.assert_not_called()

    def test_invalid_retained_backup_stops_before_source_save_and_is_not_overwritten(self):
        _, _, state = self.prepared_release_change()
        original = self.source.read_bytes()
        item = state["sourceCandidates"][0]
        backup = self.output / ("backup-" + item["originalSha256"] + "-" + self.source.name)
        launcher.write_private(backup, b"different prior backup")
        with patch.object(launcher, "native_command", return_value=0), self.assertRaisesRegex(launcher.Prerequisite, "approved_backup_changed"):
            launcher.apply_native(self.deployment)
        self.assertEqual(backup.read_bytes(), b"different prior backup")
        self.assertEqual(self.source.read_bytes(), original)
        self.deployment.docker.run.assert_not_called()

    def test_exact_reviewed_staged_bytes_retain_source_mode_and_apply_only_approved_service(self):
        candidate, reviewed, state = self.prepared_release_change()
        original = self.source.read_bytes()
        self.source.chmod(0o640)
        observed_modes = []
        def observe(*arguments):
            observed_modes.extend(path.stat().st_mode & 0o777 for path in self.directory.glob(".rateldesk-reviewed-*"))
        self.deployment.observe_runtime.side_effect = observe
        self.deployment.docker.run = Mock(return_value=b"")
        with patch.object(launcher, "native_command", return_value=0):
            self.assertEqual(launcher.apply_native(self.deployment), 0)
        self.assertEqual(observed_modes, [0o600])
        self.assertEqual(self.source.read_bytes(), reviewed)
        self.assertEqual(candidate.read_bytes(), reviewed)
        self.assertEqual(self.source.stat().st_mode & 0o777, 0o640)
        self.deployment.docker.run.assert_called_once_with(self.deployment.compose + ["up", "--pull", "never", "--no-build", "-d", "--no-deps", "mcp-http"],
            maximum=16384, owner_tool=True)
        backup = self.output / ("backup-" + state["sourceCandidates"][0]["originalSha256"] + "-" + self.source.name)
        self.assertEqual(backup.read_bytes(), original)
        self.assertEqual(launcher.read_private_json(self.output / "launcher-state.json")["phase"], "applied-verify-pending")

    def test_owner_source_transition_requires_exact_reviewed_candidate_with_unchanged_dependencies(self):
        args = launcher.parser().parse_args(["--compose-file", str(self.source), "--project", "fixture",
            "--manager", "external-compose", "--output-directory", str(self.output), "--intent", "prepare-changes"])
        class SourceDocker:
            def run(self, arguments, **kwargs):
                if arguments[0] == "ps":
                    return b""
                raise AssertionError(arguments)
        deployment = launcher.Deployment(args, SourceDocker())
        dependency = self.directory / "service.env"
        dependency.write_text("SYNTHETIC=original\n")
        original = [launcher.file_record(self.source), launcher.file_record(dependency)]
        reviewed = b"# operator formatting\nservices:\n  mcp-http: {image: synthetic:reviewed}\n"
        state = {"sources": original, "sourceCandidates": [{"source": str(self.source),
            "originalSha256": original[0]["sha256"], "candidateSha256": hashlib.sha256(reviewed).hexdigest()}]}
        launcher.write_private(self.output / "launcher-state.json", state)
        deployment.validate_resume_sources(original, True)
        self.source.write_bytes(reviewed)
        deployment.validate_resume_sources([launcher.file_record(self.source), launcher.file_record(dependency)], True)
        dependency.write_text("SYNTHETIC=changed\n")
        with self.assertRaisesRegex(launcher.Prerequisite, "approved_sources_changed"):
            deployment.validate_resume_sources([launcher.file_record(self.source), launcher.file_record(dependency)], True)
        self.assertEqual(launcher.read_private_json(self.output / "launcher-state.json"), state)

    def test_actual_stale_bind_bytes_cannot_be_substituted_with_current_saved_provider(self):
        for container_path in ("/app/appsettings.json", "/run/rateldesk/config.json"):
            for stale in (True, False):
                with self.subTest(container_path=container_path, stale=stale):
                    host = self.directory / ("appsettings.json" if container_path.startswith("/app/") else "outbound.json")
                    current = b'{"Synthetic":"current saved provider"}'
                    host.write_bytes(current)
                    serving = b'{"Synthetic":"old mounted inode"}' if stale else current
                    operations = []
                    class RunningProviderDocker:
                        def run(self, arguments, **kwargs):
                            operations.append(arguments)
                            if arguments[0] == "exec":
                                if arguments[-2] == "provider-presence":
                                    return b"present" if arguments[-1] == container_path else b"absent"
                                return b""  # regular and readable as serving user
                            if arguments[0] == "cp":
                                if arguments[1] == "running:" + container_path:
                                    Path(arguments[2]).write_bytes(serving)
                                    return b""
                                return None
                            raise AssertionError("Unexpected deployment operation: " + str(arguments[0]))
                    deployment = object.__new__(launcher.Deployment)
                    deployment.legacy, deployment.output = False, self.output
                    deployment.docker = RunningProviderDocker()
                    deployment.provider_source_records = {}
                    deployment.rendered = {}
                    deployment.roles, deployment.project = {"mcp-http": "mcp-http"}, "fixture"
                    service = {"volumes": [{"type": "bind", "source": str(host), "target": container_path}],
                        "environment": {"Helpdesk__Mcp__ConfigurationPath": container_path} if container_path.startswith("/run/") else {}}
                    if stale:
                        with self.assertRaisesRegex(launcher.Prerequisite, "runtime_provider_mismatch"):
                            deployment.configuration_providers("mcp-http", service, "synthetic-image", [], container="running")
                    else:
                        providers = deployment.configuration_providers("mcp-http", service, "synthetic-image", [], container="running")
                        self.assertEqual(providers[-1]["content"], current.decode())
                    self.assertEqual(host.read_bytes(), current)
                    self.assertTrue(any(arguments[0] == "exec" for arguments in operations))
                    self.assertTrue(any(arguments[0] == "cp" for arguments in operations))
                    self.assertTrue(all(arguments[0] in ("exec", "cp") for arguments in operations))

    def test_runtime_capture_binds_mount_records_independently_of_engine_map_iteration_order(self):
        mounts = [{"Type": "bind", "Source": "/operator/a", "Destination": "/app/a", "RW": False, "Propagation": "rprivate"},
            {"Type": "bind", "Source": "/operator/b", "Destination": "/app/b", "RW": True, "Propagation": "rprivate"},
            {"Type": "volume", "Name": "fixture_data", "Source": "/var/lib/docker/volumes/fixture_data/_data",
             "Destination": "/data", "RW": True, "Propagation": ""}]
        definition = {"image": "synthetic-image", "volumes": [
            {"type": "bind", "source": "/operator/a", "target": "/app/a", "read_only": True},
            {"type": "bind", "source": "/operator/b", "target": "/app/b"},
            {"type": "volume", "source": "data", "target": "/data"}]}
        metadata = {"Image": "sha256:synthetic", "Config": {"Env": [],
            "Labels": {"com.docker.compose.project": "fixture", "com.docker.compose.service": "mcp-http"}},
            "HostConfig": {"RestartPolicy": {"Name": "no"}}, "State": {"Status": "running", "StartedAt": "unchanged", "ExitCode": 0},
            "RestartCount": 0, "Mounts": mounts, "NetworkSettings": {"Networks": {"fixture_default": {}}}}
        docker = SimpleNamespace(run=lambda *arguments, **kwargs: b'"container"\n',
            inspect=lambda identifier: json.loads(json.dumps(metadata)),
            json=lambda arguments: {"Id": "sha256:synthetic", "Config": {"Env": []}})
        deployment = object.__new__(launcher.Deployment)
        deployment.docker, deployment.project, deployment.rendered = docker, "fixture", {"volumes": {"data": {}}}
        proposal = {"services": {"mcp-http": definition}}
        baseline = deployment.runtime_inventory(proposal)
        self.assertCountEqual(baseline["mcp-http"]["mounts"], mounts)
        for order in (mounts[1:] + mounts[:1], list(reversed(mounts))):
            metadata["Mounts"] = order
            self.assertEqual(baseline, deployment.runtime_inventory(proposal))
            self.assertEqual(metadata["Mounts"], order, "Capture must not rewrite Engine observations")
        metadata["Mounts"] = json.loads(json.dumps(mounts))
        metadata["Mounts"][0]["Propagation"] = "rshared"
        changed = deployment.runtime_inventory(proposal)
        self.assertNotEqual(launcher.stable_runtime_inventory(baseline), launcher.stable_runtime_inventory(changed))
        metadata["Mounts"][0]["Source"] = "/unreviewed"
        with self.assertRaisesRegex(launcher.Prerequisite, "runtime_mount_mismatch"):
            deployment.runtime_inventory(proposal)

    def test_rendered_literal_dollars_match_engine_values_without_changing_image_defaults(self):
        raw = {"services": {"mcp-http": {"image": "synthetic-image", "environment": {"SOURCE": "$$SOURCE/$$$$/plain$"},
            "command": ["echo", "$$COMMAND"], "entrypoint": ["echo", "$$ENTRYPOINT"],
            "healthcheck": {"test": ["CMD-SHELL", "pg_isready -U $$POSTGRES_USER; echo $$$$"]}}}}
        original = json.dumps(raw, sort_keys=True)
        model = launcher.compose_runtime_model(raw)
        definition = model["services"]["mcp-http"]
        self.assertEqual(json.dumps(raw, sort_keys=True), original, "Discovery must not rewrite raw rendered/source data")
        self.assertEqual(definition["environment"]["SOURCE"], "$SOURCE/$$/plain$")
        self.assertEqual(definition["command"], ["echo", "$COMMAND"])
        self.assertEqual(definition["entrypoint"], ["echo", "$ENTRYPOINT"])
        self.assertEqual(definition["healthcheck"]["test"], ["CMD-SHELL", "pg_isready -U $POSTGRES_USER; echo $$"])
        image = {"Id": "sha256:synthetic", "Config": {"Env": ["IMAGE=$$IMAGE"],
            "Cmd": ["echo", "$$IMAGE_COMMAND"], "Entrypoint": ["echo", "$$IMAGE_ENTRYPOINT"],
            "Healthcheck": {"Test": ["CMD-SHELL", "echo $$IMAGE_HEALTH"]}}}
        metadata = {"Image": image["Id"], "Config": {"Env": ["IMAGE=$$IMAGE", "SOURCE=$SOURCE/$$/plain$"],
            "Cmd": definition["command"], "Entrypoint": definition["entrypoint"],
            "Healthcheck": {"Test": definition["healthcheck"]["test"]},
            "Labels": {"com.docker.compose.project": "fixture", "com.docker.compose.service": "mcp-http"}},
            "HostConfig": {"RestartPolicy": {"Name": "no"}}, "Mounts": [],
            "NetworkSettings": {"Networks": {"fixture_default": {}}}}
        deployment = SimpleNamespace(docker=SimpleNamespace(json=lambda arguments: image), project="fixture", rendered={})
        launcher.validate_runtime_topology(deployment, "mcp-http", definition, metadata)
        for field, changed, code in (("Env", ["IMAGE=$$IMAGE", "SOURCE=unreviewed"], "runtime_environment_mismatch"),
                ("Cmd", ["echo", "unreviewed"], "runtime_command_mismatch"),
                ("Entrypoint", ["echo", "unreviewed"], "runtime_command_mismatch"),
                ("Healthcheck", {"Test": ["CMD-SHELL", "unreviewed"]}, "runtime_healthcheck_mismatch")):
            altered = json.loads(json.dumps(metadata))
            altered["Config"][field] = changed
            with self.subTest(field=field), self.assertRaisesRegex(launcher.Prerequisite, code):
                launcher.validate_runtime_topology(deployment, "mcp-http", definition, altered)
        inherited = json.loads(json.dumps(metadata))
        inherited["Config"].update({"Env": image["Config"]["Env"], "Cmd": image["Config"]["Cmd"],
            "Entrypoint": image["Config"]["Entrypoint"], "Healthcheck": image["Config"]["Healthcheck"]})
        launcher.validate_runtime_topology(deployment, "mcp-http", {"image": "synthetic-image"}, inherited)

    def test_runtime_user_and_working_directory_are_observed_instead_of_copied_from_source(self):
        definition = {"image": "synthetic-image", "user": "10001:10001", "working_dir": "/app", "networks": {"default": {}}}
        image = {"Id": "sha256:synthetic", "Config": {"User": "10001:10001", "WorkingDir": "/app", "Env": []}}
        metadata = {"Image": image["Id"], "Config": {"User": "10001:10001", "WorkingDir": "/app", "Env": [],
            "Labels": {"com.docker.compose.project": "fixture", "com.docker.compose.service": "mcp-http"}},
            "HostConfig": {"RestartPolicy": {"Name": "no"}}, "Mounts": [],
            "NetworkSettings": {"Networks": {"fixture_default": {}}}}
        deployment = SimpleNamespace(docker=SimpleNamespace(json=lambda arguments: image), project="fixture", rendered={})
        launcher.validate_runtime_topology(deployment, "mcp-http", definition, metadata)
        for field, wrong, code in (("User", "0:0", "runtime_user_mismatch"), ("WorkingDir", "/different-root", "runtime_working_directory_mismatch")):
            changed = json.loads(json.dumps(metadata))
            changed["Config"][field] = wrong
            with self.subTest(field=field), self.assertRaisesRegex(launcher.Prerequisite, code):
                launcher.validate_runtime_topology(deployment, "mcp-http", definition, changed)


class RuntimeOptionContracts(unittest.TestCase):
    def test_configured_runtime_constraints_match_actual_inspect_values_and_detect_independent_changes(self):
        cases = [
            ("dns", ["192.0.2.53"], "Dns", ["192.0.2.53"], ["192.0.2.54"]),
            ("dns_search", ["example.test"], "DnsSearch", ["example.test"], ["other.test"]),
            ("dns_opt", ["use-vc"], "DnsOptions", ["use-vc"], []),
            ("extra_hosts", ["fixture.example.test=::1"], "ExtraHosts", ["fixture.example.test:::1"], []),
            ("sysctls", {"net.ipv4.ip_forward": "1"}, "Sysctls", {"net.ipv4.ip_forward": "1"}, {"net.ipv4.ip_forward": "0"}),
            ("devices", [{"source": "/dev/null", "target": "/dev/xnull", "permissions": "rw"}], "Devices",
                [{"PathOnHost": "/dev/null", "PathInContainer": "/dev/xnull", "CgroupPermissions": "wr"}], []),
            ("ulimits", {"nofile": {"soft": 1024, "hard": 2048}}, "Ulimits", [{"Name": "nofile", "Soft": 1024, "Hard": 2048}], []),
            ("pid", "host", "PidMode", "host", ""), ("ipc", "shareable", "IpcMode", "shareable", "private"),
            ("userns_mode", "host", "UsernsMode", "host", ""),
            ("mem_limit", "134217728", "Memory", 134217728, 0),
            ("mem_reservation", "67108864", "MemoryReservation", 67108864, 0),
            ("memswap_limit", "-1", "MemorySwap", -1, 0),
            ("mem_swappiness", 60, "MemorySwappiness", 60, None),
            ("shm_size", "33554432", "ShmSize", 33554432, 67108864),
            ("pids_limit", 100, "PidsLimit", 100, -1),
            ("cpu_period", 100000, "CpuPeriod", 100000, 0),
            ("cpu_quota", 50000, "CpuQuota", 50000, 0),
            ("cpu_shares", 512, "CpuShares", 512, 1024),
            ("cpus", 0.5, "NanoCpus", 500000000, 1000000000),
            ("cpuset", "0,2", "CpusetCpus", "0,2", "0"),
            ("oom_score_adj", 100, "OomScoreAdj", 100, 0),
            ("oom_kill_disable", True, "OomKillDisable", True, False),
            ("init", True, "Init", True, False),
            ("storage_opt", {"size": "1G"}, "StorageOpt", {"size": "1G"}, {}),
        ]
        for key, configured, field, matching, wrong in cases:
            with self.subTest(key=key):
                definition, metadata = {key: configured}, {"HostConfig": {field: matching}}
                snapshot = launcher.validate_runtime_options(definition, metadata)
                self.assertEqual(snapshot["host"][field], matching)
                metadata["HostConfig"][field] = wrong
                with self.assertRaisesRegex(launcher.Prerequisite, "runtime_option_mismatch") as failure:
                    launcher.validate_runtime_options(definition, metadata)
                self.assertIn(key, str(failure.exception))

    def test_absent_source_options_preserve_inherited_daemon_defaults_in_baseline(self):
        actual = {"Dns": ["192.0.2.53"], "IpcMode": "private", "ShmSize": 67108864,
            "Sysctls": {"net.ipv4.ip_unprivileged_port_start": "0"}, "MemorySwappiness": None}
        snapshot = launcher.validate_runtime_options({}, {"HostConfig": actual})
        self.assertEqual({key: snapshot["host"][key] for key in actual}, actual)
        changed = launcher.validate_runtime_options({}, {"HostConfig": {**actual, "Dns": ["192.0.2.54"]}})
        self.assertNotEqual(snapshot, changed)

    def test_configured_sysctl_and_ulimit_allow_additional_unconfigured_daemon_defaults(self):
        definition = {"sysctls": {"net.ipv4.ip_forward": "1"}, "ulimits": {"nofile": 1024}}
        metadata = {"HostConfig": {"Sysctls": {"net.ipv4.ip_forward": "1", "net.ipv4.ip_unprivileged_port_start": "0"},
            "Ulimits": [{"Name": "nofile", "Soft": 1024, "Hard": 1024}, {"Name": "nproc", "Soft": 2048, "Hard": 2048}]}}
        launcher.validate_runtime_options(definition, metadata)

    def test_deploy_resource_constraints_are_checked_against_real_host_limits(self):
        source = {"deploy": {"resources": {"limits": {"cpus": "0.25", "memory": "134217728", "pids": 100},
            "reservations": {"memory": "67108864"}}}}
        host = {"NanoCpus": 250000000, "Memory": 134217728, "PidsLimit": 100, "MemoryReservation": 67108864}
        launcher.validate_runtime_options(source, {"HostConfig": host})
        for field in host:
            with self.subTest(field=field), self.assertRaisesRegex(launcher.Prerequisite, "runtime_option_mismatch"):
                launcher.validate_runtime_options(source, {"HostConfig": {**host, field: 0}})

    def test_unresolved_configured_options_report_exact_key_without_rewriting_or_echoing_values(self):
        cases = [("pid", {"pid": "service:private-name"}), ("ipc", {"ipc": "container:private-name"}),
            ("devices", {"devices": [{"source": "vendor.example/private-device=all"}]}),
            ("deploy.resources.reservations.devices", {"deploy": {"resources": {"reservations": {"devices": [{"driver": "private-driver"}]}}}}),
            ("deploy.resources.reservations.cpus", {"deploy": {"resources": {"reservations": {"cpus": "0.25"}}}}),
            ("gpus", {"gpus": "private-gpu"}), ("use_api_socket", {"use_api_socket": True}),
            ("blkio_config", {"blkio_config": {"weight": 100}})]
        for key, source in cases:
            with self.subTest(key=key), self.assertRaisesRegex(launcher.Prerequisite, "runtime_option_unsupported") as failure:
                launcher.validate_runtime_options(source, {"HostConfig": {}})
            self.assertIn(key, str(failure.exception))
            self.assertIn("Keep the original saved source", str(failure.exception))
            self.assertNotIn("private-name", str(failure.exception))
            self.assertNotIn("private-driver", str(failure.exception))


if __name__ == "__main__":
    unittest.main()

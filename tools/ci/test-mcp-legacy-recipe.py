#!/usr/bin/env python3
"""Legacy source-recipe fences using real Compose config, without a daemon."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("legacy_recipe_launcher", ROOT / "tools/mcp/setup-http-mcp.py")
launcher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(launcher)


class ConfigOnlyDocker(launcher.Docker):
    def __init__(self, directory):
        super().__init__()
        self.selection = {"DOCKER_CONFIG": str(directory / "synthetic-docker-config")}
        self.daemon_identity = "synthetic-no-daemon"
        self.calls, self.hook = [], None
        self.compose_version = self.run(["compose", "version", "--short"]).decode().strip()

    def bind_daemon(self):
        pass  # Config-only fixture: any actual daemon operation is forbidden.

    def run(self, arguments, **kwargs):
        self.calls.append((list(arguments), kwargs.get("owner_tool", False)))
        if arguments[0] == "compose":
            result = super().run(arguments, **kwargs)
            if self.hook:
                self.hook(arguments)
            return result
        if arguments[0] == "run" and "remove-observation" in arguments:
            return b""  # Private scratch staging/cleanup is replaced in this fixture.
        raise AssertionError("A config-only test attempted a Docker resource operation")


class LegacyRecipeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="rateldesk legacy recipe ")
        self.directory = Path(self.temporary.name)
        self.source = self.directory / "operator source.json"
        self.environment = self.directory / "interpolation.env"
        self.environment.write_text("RECIPE_VALUE='${unchanged}/$$/single$'\n")
        self.service_env = self.directory / "service values.env"
        self.service_env.write_text("FROM_FILE=kept\n")
        self.outbound = self.directory / "legacy outbound.json"
        self.outbound.write_text('{"apiBaseUrl":"http://api:8222/","credentialMode":"gateway"}')
        source = {"services": {name: {"image": "synthetic-" + name} for name in ("api", "web", "mcp-http", "db")},
            "configs": {"retired": {"file": str(self.outbound)}}}
        source["services"]["web"].update({"env_file": [str(self.service_env)],
            "environment": {"INTERPOLATED": "${RECIPE_VALUE}"}})
        source["services"]["mcp-http"].update({"configs": [{"source": "retired", "target": "/run/rateldesk-mcp/config.json"}],
            "environment": {"LITERAL": "$${KEEP}/$$$$/single$$"}, "command": ["echo", "$$COMMAND/$$$$"],
            "entrypoint": ["echo", "$$ENTRYPOINT/$$$$"], "healthcheck": {"test": ["CMD-SHELL", "echo $$HEALTH; echo $$$$"]}})
        launcher.write_private(self.source, source)
        args = launcher.parser().parse_args(["--compose-file", str(self.source), "--env-file", str(self.environment),
            "--project", "rdk-legacy-recipe", "--project-directory", str(self.directory), "--manager", "release-compose",
            "--intent", "legacy-initializer", "--image", "synthetic-mcp-http"])
        self.docker = ConfigOnlyDocker(self.directory)
        self.deployment = launcher.Deployment(args, self.docker)
        self.deployment.image = "synthetic-mcp-http"
        self.deployment.storage = {}
        self.deployment.secret_mounts = []
        self.deployment.metadata = None
        self.output = self.deployment.output
        original = self.docker.json(self.deployment.compose + ["config", "--no-interpolate", "--no-env-resolution", "--no-path-resolution", "--format", "json"])
        launcher.write_private(self.output / ".compose-source.json", original)
        self.original = (self.output / ".compose-source.json").read_bytes()
        launcher.write_private(self.output / "approved-compose-source.json", self.original)
        frozen = self.docker.json(self.deployment.compose + ["config", "--format", "json"])
        frozen["x-rateldesk-source-files"] = [launcher.file_record(path) for path in
            (self.source, self.environment, self.service_env, self.outbound)]
        frozen["x-rateldesk-legacy-export-recipe"] = launcher.legacy_recipe_identity(self.deployment, hashlib.sha256(self.original).hexdigest())
        self.frozen = frozen
        self.deployment.proposal = copy.deepcopy(frozen)
        launcher.write_private(self.output / "approved-effective-deployment.json", frozen)
        runtime = {"type": "volume", "source": "runtime", "target": "/run/rateldesk-mcp"}
        self.fragment = {"services": {"mcp-http": {"volumes": [{**runtime, "read_only": True}],
                "depends_on": {"mcp-config-init": {"condition": "service_completed_successfully"}}},
            "mcp-config-init": {"image": "synthetic-mcp-http", "entrypoint": ["/bin/sh", "-ec"],
                "command": ["true"], "volumes": [runtime]}}, "volumes": {"runtime": {}},
            "x-rateldesk-mount-migrations": [{"service": "mcp-http", "kind": "configs", "source": "retired",
                "target": "/run/rateldesk-mcp/config.json"}]}
        approved = copy.deepcopy(frozen)
        approved["services"]["mcp-http"]["configs"] = []
        approved["services"]["mcp-http"].update(copy.deepcopy(self.fragment["services"]["mcp-http"]))
        approved["services"]["mcp-config-init"] = copy.deepcopy(self.fragment["services"]["mcp-config-init"])
        approved.setdefault("volumes", {}).update(self.fragment["volumes"])
        launcher.write_private(self.output / "approved-proposal.json", approved)
        launcher.write_private(self.output / "compose.host.json", self.fragment)
        launcher.write_private(self.output / "mcp-outbound.json", self.outbound.read_bytes())
        names = ("approved-proposal.json", "approved-effective-deployment.json", "compose.host.json", "mcp-outbound.json")
        receipt = {"approved": True, "intent": 2, "approvedBy": "synthetic-admin",
            "sourceHash": hashlib.sha256((self.output / "approved-effective-deployment.json").read_bytes()).hexdigest(),
            "proposalHash": "a" * 64, "exportHashes": {name: hashlib.sha256((self.output / name).read_bytes()).hexdigest() for name in names}}
        launcher.write_private(self.output / "host-approval.json", receipt)
        raw_paths = [self.output / name for name in (".test-source.json", ".test-patch.json")]
        self.expected = launcher.render_legacy_recipe(self.deployment, self.original, frozen,
            (self.output / "compose.host.json").read_bytes(), raw_paths)
        launcher.write_private(self.output / "deployment.json", self.expected)
        for path in raw_paths:
            path.unlink()
        self.docker.calls.clear()
        self.staged = {}

    def tearDown(self):
        self.temporary.cleanup()

    def observe(self, sources=None):
        return launcher.legacy_compose_observation(self.deployment, sources or [self.output / "deployment.json"])

    def stage(self, deployment, name):
        self.staged[name] = (deployment.output / name).read_bytes()

    def test_fresh_recipe_preserves_source_only_definitions_and_literal_interpolation_once(self):
        before = self.source.read_bytes()
        with patch.object(launcher, "stage_snapshot", side_effect=self.stage), self.observe() as actual:
            proof = dict(self.deployment.legacy_compose_proof)
            self.assertEqual(7, len(proof))
            self.assertEqual(hashlib.sha256(self.expected).hexdigest(), proof["RATELDESK_SETUP_REVIEWED_DEPLOYMENT_SHA256"])
            self.assertEqual(hashlib.sha256((self.output / "compose.host.json").read_bytes()).hexdigest(),
                proof["RATELDESK_SETUP_APPROVED_OVERLAY_SHA256"])
            self.assertEqual("$${unchanged}/$$$$/single$$", actual["services"]["web"]["environment"]["INTERPOLATED"])
            self.assertEqual("$${KEEP}/$$$$/single$$", actual["services"]["mcp-http"]["environment"]["LITERAL"])
            self.assertEqual(["echo", "$$COMMAND/$$$$"], actual["services"]["mcp-http"]["command"])
            self.assertNotIn("configs", actual)
            self.assertIn("retired", json.loads(self.expected)["configs"])
            expected_model, actual_model = [json.loads(content) for content in self.staged.values()]
            self.assertEqual(expected_model, actual_model)
        self.assertEqual({}, self.deployment.legacy_compose_proof)
        self.assertEqual([], list(self.output.glob(".legacy-observation-*")))
        self.assertEqual(before, self.source.read_bytes())
        raw_calls = [arguments for arguments, _ in self.docker.calls if "--no-env-resolution" in arguments]
        self.assertEqual(1, len(raw_calls))
        self.assertTrue(all(flag in raw_calls[0] for flag in ("--no-interpolate", "--no-env-resolution", "--no-path-resolution")))
        self.assertTrue(any(owner and arguments[-3:] == ["config", "--format", "json"] for arguments, owner in self.docker.calls))

    def test_source_only_or_project_name_edits_cannot_be_self_rebased_with_state_hashes(self):
        for target in ("unused-config", "project-name"):
            with self.subTest(target=target):
                document = json.loads(self.expected)
                if target == "unused-config":
                    document["configs"]["retired"]["file"] = str(self.directory / "unreviewed.json")
                else:
                    document["name"] = "unreviewed-project"
                launcher.write_private(self.output / "deployment.json", document)
                altered = (self.output / "deployment.json").read_bytes()
                launcher.write_private(self.output / "launcher-state.json", {"deploymentSha256": hashlib.sha256(altered).hexdigest(),
                    "resolvedDeploymentSha256": launcher.resolved_deployment_hash(document)})
                with patch.object(launcher, "stage_snapshot", side_effect=self.stage), \
                        self.assertRaisesRegex(launcher.Prerequisite, "legacy_recipe_changed"):
                    with self.observe():
                        self.fail("Altered raw manager document reached validation")
                self.assertEqual({}, self.staged)
                launcher.write_private(self.output / "deployment.json", self.expected)

    def test_retained_original_recipe_and_local_annotation_cannot_renew_approval(self):
        launcher.write_private(self.output / "approved-compose-source.json", {"services": {"unexpected": {"image": "synthetic"}}})
        launcher.write_private(self.output / "launcher-state.json", {"unresolvedSourceSha256": hashlib.sha256(
            (self.output / "approved-compose-source.json").read_bytes()).hexdigest()})
        with self.assertRaisesRegex(launcher.Prerequisite, "legacy_recipe_invalid"):
            with self.observe():
                self.fail("Changed original recipe reached rendering")
        self.assertFalse(any("config" in arguments for arguments, _ in self.docker.calls))

    def test_older_receipt_without_bound_recipe_fails_with_unchanged_readoption_recovery(self):
        old_source = copy.deepcopy(self.frozen)
        old_source.pop("x-rateldesk-legacy-export-recipe")
        launcher.write_private(self.output / "approved-effective-deployment.json", old_source)
        receipt = launcher.read_private_json(self.output / "host-approval.json")
        receipt.pop("intent")  # beta.2 defaults to the explicit initializer intent.
        digest = hashlib.sha256((self.output / "approved-effective-deployment.json").read_bytes()).hexdigest()
        receipt["sourceHash"] = digest.upper()
        receipt["exportHashes"]["approved-effective-deployment.json"] = digest.upper()
        launcher.write_private(self.output / "host-approval.json", receipt)
        with self.assertRaisesRegex(launcher.Prerequisite, "legacy_recipe_invalid") as failure:
            with self.observe():
                self.fail("An unbound old recipe reached owner apply")
        self.assertIn("Re-adopt the running deployment unchanged", str(failure.exception))
        self.assertFalse(any("config" in arguments for arguments, _ in self.docker.calls))

    def test_raw_recipe_drift_during_projection_is_rejected_before_tool_invocation(self):
        def change_after_projection(arguments):
            if arguments[-3:] == ["--format", "json"] or arguments[-3:] == ["config", "--format", "json"]:
                if any(str(value).endswith("-input.json") for value in arguments):
                    (self.output / "approved-compose-source.json").write_bytes(self.original + b' ')
        self.docker.hook = change_after_projection
        with patch.object(launcher, "stage_snapshot", side_effect=self.stage), \
                self.assertRaisesRegex(launcher.Prerequisite, "legacy_recipe_changed"):
            with self.observe():
                self.fail("Changed input reached C# invocation")
        self.assertEqual({}, self.deployment.legacy_compose_proof)
        self.assertEqual([], list(self.output.glob(".legacy-observation-*")))

    def test_owner_syntax_acceptance_without_same_rendered_semantics_is_rejected(self):
        original_run = self.docker.run
        def different_owner(arguments, **kwargs):
            content = original_run(arguments, **kwargs)
            if kwargs.get("owner_tool") and arguments[-3:] == ["config", "--format", "json"]:
                model = json.loads(content)
                model["services"]["mcp-config-init"]["networks"] = {"unreviewed-owner-network": None}
                return json.dumps(model).encode()
            return content
        with patch.object(self.docker, "run", side_effect=different_owner), \
                patch.object(launcher, "stage_snapshot", side_effect=self.stage), \
                self.assertRaisesRegex(launcher.Prerequisite, "legacy_owner_semantics"):
            with self.observe():
                self.fail("Different owner semantics reached validator")
        self.assertEqual({}, self.staged)

    def test_approved_render_input_and_tool_identity_are_fenced_after_render(self):
        for changed in ("input", "owner-version", "context"):
            with self.subTest(changed=changed):
                selection = dict(self.docker.selection)
                self.docker.hook = None
                changed_once = []
                def during_render(arguments):
                    if not changed_once and any(str(value).endswith("-input.json") for value in arguments):
                        changed_once.append(True)
                        if changed == "input":
                            path = Path(arguments[arguments.index("-f") + 1])
                            path.write_bytes(path.read_bytes() + b' ')
                        elif changed == "owner-version":
                            self.docker.compose_version = "unreviewed-renderer-version"
                        else:
                            self.docker.selection["DOCKER_HOST"] = "unix:///unreviewed-context.sock"
                self.docker.hook = during_render
                with patch.object(launcher, "stage_snapshot", side_effect=self.stage), self.assertRaisesRegex(launcher.Prerequisite, "legacy_recipe_changed"):
                    with self.observe():
                        self.fail("Changed render input/tool/context reached validator")
                self.docker.selection = selection
                self.docker.compose_version = self.frozen["x-rateldesk-legacy-export-recipe"]["rendererVersion"]
                self.docker.hook = None
                self.staged.clear()

    def test_exact_complete_import_is_supported_without_replacing_retained_original(self):
        self.source.write_bytes(self.expected)
        self.deployment.proposal["x-rateldesk-source-files"] = [launcher.file_record(path) for path in
            (self.source, self.environment, self.service_env, self.outbound)]
        with patch.object(launcher, "stage_snapshot", side_effect=self.stage), \
                launcher.legacy_compose_observation(self.deployment) as actual:
            self.assertEqual(5, len(actual["services"]))
        self.assertEqual(self.original, (self.output / "approved-compose-source.json").read_bytes())

    def test_authority_window_raw_edit_cannot_reach_owner_apply(self):
        launcher.write_private(self.output / "effective-deployment.json", self.frozen)
        state = {"sources": self.frozen["x-rateldesk-source-files"], "exportHashes": {}, "phase": "saved-apply-pending",
            "sourceHash": hashlib.sha256((self.output / "effective-deployment.json").read_bytes()).hexdigest(),
            "deploymentSha256": hashlib.sha256(self.expected).hexdigest(), "storage": {}}
        launcher.write_private(self.output / "launcher-state.json", state)
        original_run = self.docker.run
        entered = []
        def authority_then_edit(arguments, **kwargs):
            if "approval-check" in arguments:
                entered.append("authority")
                document = json.loads(self.expected)
                document["configs"]["retired"]["file"] = "unreviewed-after-authority.json"
                launcher.write_private(self.output / "deployment.json", document)
                return b"approved"
            if arguments[0] == "compose" and "up" in arguments:
                entered.append("apply")
                return b""
            return original_run(arguments, **kwargs)
        with patch.object(self.deployment, "prepare_storage"), patch.object(launcher, "stage_snapshot", side_effect=self.stage), \
                patch.object(self.docker, "run", side_effect=authority_then_edit), \
                self.assertRaisesRegex(launcher.Prerequisite, "legacy_recipe_changed"):
            launcher.apply_saved(self.deployment)
        self.assertEqual(["authority"], entered)
        self.assertEqual("saved-apply-pending", launcher.read_private_json(self.output / "launcher-state.json")["phase"])

    def test_successful_apply_uses_protected_recipe_before_scratch_cleanup(self):
        launcher.write_private(self.output / "effective-deployment.json", self.frozen)
        state = {"sources": self.frozen["x-rateldesk-source-files"], "exportHashes": {}, "phase": "saved-apply-pending",
            "sourceHash": hashlib.sha256((self.output / "effective-deployment.json").read_bytes()).hexdigest(),
            "deploymentSha256": hashlib.sha256(self.expected).hexdigest(), "storage": {}}
        launcher.write_private(self.output / "launcher-state.json", state)
        original_run = self.docker.run
        order = []
        def recorded(arguments, **kwargs):
            if "approval-check" in arguments:
                order.append("authority")
                return b"approved"
            if arguments[0] == "compose" and "up" in arguments:
                self.assertEqual(self.expected, (self.output / "deployment.json").read_bytes())
                self.assertTrue(self.deployment.legacy_compose_proof)
                self.assertIn("--no-build", arguments)
                self.assertEqual("never", arguments[arguments.index("--pull") + 1])
                order.append("apply")
                return b""
            if "remove-observation" in arguments:
                order.append("cleanup")
            return original_run(arguments, **kwargs)
        with patch.object(self.deployment, "prepare_storage"), patch.object(launcher, "stage_snapshot", side_effect=self.stage), \
                patch.object(self.docker, "run", side_effect=recorded):
            self.assertEqual(0, launcher.apply_saved(self.deployment))
        self.assertEqual(["authority", "apply", "cleanup"], order)
        self.assertEqual("applied-verify-pending", launcher.read_private_json(self.output / "launcher-state.json")["phase"])

    def test_export_and_local_receipt_rebase_during_authority_stops_before_apply(self):
        launcher.write_private(self.output / "effective-deployment.json", self.frozen)
        state = {"sources": self.frozen["x-rateldesk-source-files"], "exportHashes": {}, "phase": "saved-apply-pending",
            "sourceHash": hashlib.sha256((self.output / "effective-deployment.json").read_bytes()).hexdigest(),
            "deploymentSha256": hashlib.sha256(self.expected).hexdigest(), "storage": {}}
        launcher.write_private(self.output / "launcher-state.json", state)
        original_run = self.docker.run
        calls = []
        def changed_during_authority(arguments, **kwargs):
            if "approval-check" in arguments:
                calls.append("authority")
                launcher.write_private(self.output / "mcp-outbound.json", {"credentialMode": "authentik", "integrationCredential": "synthetic-unreviewed"})
                receipt = launcher.read_private_json(self.output / "host-approval.json")
                receipt["exportHashes"]["mcp-outbound.json"] = hashlib.sha256((self.output / "mcp-outbound.json").read_bytes()).hexdigest()
                launcher.write_private(self.output / "host-approval.json", receipt)
                return b"approved-before-file-changed"
            if arguments[0] == "compose" and "up" in arguments:
                calls.append("apply")
                return b""
            return original_run(arguments, **kwargs)
        with patch.object(self.deployment, "prepare_storage"), patch.object(launcher, "stage_snapshot", side_effect=self.stage), \
                patch.object(self.docker, "run", side_effect=changed_during_authority), \
                self.assertRaisesRegex(launcher.Prerequisite, "legacy_recipe_changed"):
            launcher.apply_saved(self.deployment)
        self.assertEqual(["authority"], calls)


if __name__ == "__main__":
    unittest.main(verbosity=2)

#!/usr/bin/env python3
"""Bounded private-tool selection contracts; no downloads or daemon mutation."""
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.error

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("mcp_launcher", ROOT / "tools/mcp/setup-http-mcp.py")
launcher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(launcher)
toolchain = launcher.compose_toolchain


class PrivateComposeContracts(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="private compose contract ")
        self.directory = Path(self.temporary.name)
        self.binary = self.directory / "verified existing compose"
        self.binary.write_bytes(b"#!/bin/sh\nexit 0\n")
        self.binary.chmod(0o700)
        self.digest = hashlib.sha256(self.binary.read_bytes()).hexdigest()
        self.pin = patch.object(toolchain, "platform_pin", return_value=("linux-x86_64", self.digest))
        self.pin.start()

    def tearDown(self):
        self.pin.stop()
        self.temporary.cleanup()

    def test_existing_selection_requires_expected_integrity_and_preserves_original_docker_configuration(self):
        config = self.directory / "original Docker configuration"
        config.mkdir()
        content = b'{"credsStore":"synthetic-helper","currentContext":"named-context","auths":{"synthetic":{"auth":"never-copy"}}}'
        (config / "config.json").write_bytes(content)
        environment = {"DOCKER_CONFIG": str(config), "DOCKER_CONTEXT": "named-context", "DOCKER_TLS_VERIFY": "1",
            "DOCKER_CERT_PATH": str(self.directory / "original tls")}
        with patch.dict(os.environ, environment, clear=True):
            docker = launcher.Docker()
            args = launcher.parser().parse_args(["--compose-binary", str(self.binary), "--compose-sha256", self.digest])
            docker.configure(args)
        self.assertEqual({key: docker.process_environment()[key] for key in environment}, environment)
        self.assertEqual((config / "config.json").read_bytes(), content)
        self.assertEqual(docker.compose_tool.command(["config"]), [str(self.binary), "config"])
        self.assertNotIn("never-copy", str(docker.selection))

    def test_missing_digest_mismatch_and_replaced_binary_are_rejected_before_execution(self):
        for expected in (None, "not-a-digest", "0" * 64):
            with self.subTest(expected=expected), self.assertRaisesRegex(launcher.Prerequisite, "compose_tool_integrity"):
                toolchain.Selection(self.binary, expected, launcher.fail)
        selection = toolchain.Selection(self.binary, self.digest, launcher.fail)
        self.binary.write_bytes(b"replaced executable")
        with self.assertRaisesRegex(launcher.Prerequisite, "compose_tool_integrity"):
            selection.command(["version"])

    def test_offline_preparation_and_reuse_never_access_network_or_copy_docker_config(self):
        destination = self.directory / "private setup tools"
        with patch.object(toolchain.urllib.request, "urlopen", side_effect=AssertionError("Offline means no network")):
            first = toolchain.prepare(destination, launcher.fail, cache=self.binary)
            second = toolchain.prepare(destination, launcher.fail)
        self.assertEqual(first.path, second.path)
        self.assertEqual(first.expected, self.digest)
        self.assertEqual(destination.stat().st_mode & 0o777, 0o700)
        self.assertEqual(first.path.stat().st_mode & 0o777, 0o700)
        self.assertEqual(list(destination.iterdir()), [first.path])

    def test_bad_offline_cache_and_bad_download_leave_no_executable_or_partial_file(self):
        for offline in (False, True):
            destination = self.directory / str(offline)
            bad = self.directory / "wrong-platform-cache"
            bad.write_bytes(b"wrong bytes")
            with self.subTest(offline=offline), patch.object(toolchain.urllib.request, "urlopen", return_value=io.BytesIO(b"wrong bytes")), \
                    self.assertRaisesRegex(launcher.Prerequisite, "compose_tool_integrity"):
                toolchain.prepare(destination, launcher.fail, cache=bad if offline else None)
            self.assertEqual(list(destination.iterdir()), [])

    def test_network_error_and_interrupted_download_remove_partial_files(self):
        for failure, expected in ((urllib.error.URLError("private proxy credential"), "compose_tool_network"),
                                  (KeyboardInterrupt(), None)):
            destination = self.directory / (expected or "interrupt")
            with patch.object(toolchain.urllib.request, "urlopen", side_effect=failure):
                if expected:
                    with self.assertRaisesRegex(launcher.Prerequisite, expected) as caught:
                        toolchain.prepare(destination, launcher.fail)
                    self.assertNotIn("private proxy", str(caught.exception))
                else:
                    with self.assertRaises(KeyboardInterrupt):
                        toolchain.prepare(destination, launcher.fail)
            self.assertEqual(list(destination.iterdir()), [])

    def test_platform_error_happens_before_any_directory_or_network(self):
        destination = self.directory / "unsupported"
        self.pin.stop()
        with patch.object(toolchain.platform, "system", return_value="Unknown"), \
                patch.object(toolchain.platform, "machine", return_value="unsupported"), \
                self.assertRaisesRegex(launcher.Prerequisite, "compose_tool_platform"):
            toolchain.prepare(destination, launcher.fail)
        self.assertFalse(destination.exists())
        self.pin.start()

    def test_cancel_is_an_explicit_supported_action_without_preparing_storage(self):
        output = io.StringIO()
        with self.assertRaisesRegex(launcher.Prerequisite, "compose_tool_cancelled") as caught:
            toolchain.choose(launcher.fail, input_fn=lambda prompt: "3", output=output)
        self.assertIn("host's supported package channel", str(caught.exception))
        self.assertIn("existing verified", output.getvalue())
        self.assertIn("explicit download consent", output.getvalue())
        self.assertEqual(list(self.directory.iterdir()), [self.binary])

    def test_interactive_existing_path_and_prepare_are_explicit_choices(self):
        answers = iter(("1", str(self.binary), self.digest))
        selected = toolchain.choose(launcher.fail, input_fn=lambda prompt: next(answers), output=io.StringIO())
        self.assertEqual(selected.path, self.binary)
        destination = self.directory / "explicit preparation"
        answers = iter(("2", str(destination)))
        with patch.object(toolchain.urllib.request, "urlopen", return_value=io.BytesIO(self.binary.read_bytes())):
            selected = toolchain.choose(launcher.fail, input_fn=lambda prompt: next(answers), output=io.StringIO())
        self.assertEqual(selected.path.parent, destination)

    def test_private_directory_and_binary_symlinks_and_open_permissions_are_rejected(self):
        public = self.directory / "unprotected"
        public.mkdir(mode=0o755)
        public.chmod(0o755)
        alias = self.directory / "alias"
        alias.symlink_to(public, target_is_directory=True)
        for destination in (public, alias):
            with self.subTest(destination=destination), self.assertRaisesRegex(launcher.Prerequisite, "compose_tool_directory"):
                toolchain.prepare(destination, launcher.fail, cache=self.binary)
        executable_alias = self.directory / "executable alias"
        executable_alias.symlink_to(self.binary)
        with self.assertRaisesRegex(launcher.Prerequisite, "compose_tool_integrity"):
            toolchain.Selection(executable_alias, self.digest, launcher.fail)

    def test_resume_receipt_roundtrips_spaces_and_detects_changed_selection(self):
        with patch.dict(os.environ, {}, clear=True):
            docker = launcher.Docker()
            docker.configure(launcher.parser().parse_args(["--compose-binary", str(self.binary), "--compose-sha256", self.digest]))
        docker.selection[toolchain.VERSION_KEY] = "2.39.4"
        docker.selection["DOCKER_CONTEXT"] = "original-context"
        docker.daemon_identity = "same-daemon"
        # Reusing the same non-secret environment receipt is the fresh-process
        # contract used by the host, client launcher, apply and --verify.
        with patch.dict(os.environ, docker.selection, clear=True):
            fresh = launcher.Docker()
        fresh.daemon_identity = docker.daemon_identity
        fresh.check_selection({"dockerSelection": docker.selection, "dockerDaemonIdentity": docker.daemon_identity})
        self.assertEqual(fresh.compose_tool.command(["config"])[0], str(self.binary))
        fresh.selection[toolchain.DIGEST_KEY] = "0" * 64
        with self.assertRaisesRegex(launcher.Prerequisite, "docker_selection_changed"):
            fresh.check_selection({"dockerSelection": docker.selection})

    def test_native_operation_preflight_does_not_inspect_or_require_compose(self):
        with patch.dict(os.environ, {"DOCKER_HOST": "unix:///synthetic"}, clear=True):
            docker = launcher.Docker()
        calls = []
        def run(arguments, **kwargs):
            calls.append(arguments)
            return b'"synthetic-daemon-id"'
        with patch.object(docker, "run", side_effect=run):
            docker.preflight(require_compose=False)
        self.assertEqual(calls, [["info", "--format", "{{json .ID}}"]])

    def test_same_named_context_with_changed_endpoint_or_tls_policy_requires_new_review(self):
        with patch.dict(os.environ, {"DOCKER_CONTEXT": "reviewed-context"}, clear=True):
            docker = launcher.Docker()
        endpoint = {"Host": "unix:///reviewed/socket", "SkipTLSVerify": False}
        def run(arguments, **kwargs):
            if arguments[:2] == ["context", "inspect"]:
                return json.dumps(endpoint).encode()
            if arguments[0] == "info":
                return b'"same-daemon-id"'
            raise AssertionError(arguments)
        with patch.object(docker, "run", side_effect=run):
            docker.bind_daemon()
            reviewed = docker.selection[toolchain.CONTEXT_KEY]
            endpoint["SkipTLSVerify"] = True
            with self.assertRaisesRegex(launcher.Prerequisite, "docker_context_changed"):
                docker.bind_daemon()
        self.assertEqual(docker.selection[toolchain.CONTEXT_KEY], reviewed)

    def test_release_owner_apply_rechecks_daemon_before_executing_the_original_plugin(self):
        with patch.dict(os.environ, {"DOCKER_CONTEXT": "reviewed-context"}, clear=True):
            docker = launcher.Docker()
        docker.daemon_identity = "reviewed-daemon"
        with patch.object(docker, "bind_daemon", side_effect=launcher.Prerequisite("docker_daemon_changed: Restore original daemon")), \
                patch.object(launcher.subprocess, "run") as process, \
                self.assertRaisesRegex(launcher.Prerequisite, "docker_daemon_changed"):
            docker.run(["compose", "-f", "reviewed source.yml", "up", "-d", "--no-deps", "mcp-http"], owner_tool=True)
        process.assert_not_called()

    def test_implicit_config_and_tls_paths_remain_original_across_new_process_home_changes(self):
        original_home = self.directory / "original operator home"
        with patch.dict(os.environ, {"HOME": str(original_home), "DOCKER_TLS_VERIFY": "1"}, clear=True):
            docker = launcher.Docker()
        expected = str(original_home / ".docker")
        self.assertEqual(docker.selection["DOCKER_CONFIG"], expected)
        self.assertEqual(docker.selection["DOCKER_CERT_PATH"], expected)
        # A printed resume receipt supplies both original paths explicitly;
        # neither configuration nor TLS private keys need to be copied.
        with patch.dict(os.environ, {"HOME": str(self.directory / "different home"), **docker.selection}, clear=True):
            fresh = launcher.Docker()
        self.assertEqual(fresh.selection, docker.selection)
        self.assertFalse(original_home.exists())
        selected_config = self.directory / "original custom config"
        with patch.dict(os.environ, {"HOME": str(original_home), "DOCKER_CONFIG": str(selected_config), "DOCKER_TLS_VERIFY": "1"}, clear=True):
            custom = launcher.Docker()
        self.assertEqual(custom.selection["DOCKER_CERT_PATH"], str(selected_config))


if __name__ == "__main__":
    unittest.main()

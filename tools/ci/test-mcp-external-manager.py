#!/usr/bin/env python3
"""Socket transport fixture tests, not a claim of live Portainer acceptance."""
import contextlib
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import io
import json
from pathlib import Path
import tempfile
import threading
import unittest

from mcp_external_manager_acceptance import PortainerOwner


class ExternalManagerTransportTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="rdk-manager-transport-")
        self.directory = Path(self.temporary.name)
        self.source = self.directory / "source.yml"
        self.candidate = self.directory / "candidate.yml"
        self.baseline = b'services:\n  web:\n    environment:\n      TIMEOUT: "20"\n'
        self.prepared = self.baseline.replace(b'"20"', b'"21"')
        self.source.write_bytes(self.baseline)
        self.candidate.write_bytes(self.prepared)
        self.events = []
        self.reject_status = 400
        self.change_on_interruption = False
        self.response_bytes = b""
        fixture = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, format, *arguments):
                pass  # Never log headers, bearer, request/body or private paths.

            def do_GET(self):
                fixture.events.append({"method": "GET", "authorized": self.headers.get("Authorization") == "Bearer synthetic-fixture-token"})
                body = json.dumps({"StackFileContent": fixture.source.read_text()}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def do_PUT(self):
                declared = int(self.headers["Content-Length"])
                received = self.rfile.read(declared)
                interrupted = len(received) < declared
                fixture.events.append({"method": "PUT", "declared": declared, "received": len(received),
                                       "eofBeforeLength": interrupted,
                                       "authorized": self.headers.get("Authorization") == "Bearer synthetic-fixture-token"})
                if interrupted:
                    if fixture.change_on_interruption:
                        fixture.source.write_bytes(fixture.prepared)
                    status = fixture.reject_status
                else:
                    payload = json.loads(received)
                    fixture.source.write_text(payload["StackFileContent"])
                    fixture.events[-1]["prune"] = payload["Prune"]
                    fixture.events[-1]["pull"] = payload["PullImage"]
                    status = 200
                self.send_response(status)
                self.send_header("Content-Length", str(len(fixture.response_bytes)))
                self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.write(fixture.response_bytes)
                self.close_connection = True

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.owner = PortainerOwner(self.directory / "owner")
        self.owner.base_url = "http://127.0.0.1:" + str(self.server.server_port)
        self.owner.token = "synthetic-fixture-token"
        self.owner.endpoint = 1
        self.owner.stack_environment[3] = [{"name": "SYNTHETIC", "value": "retained"}]
        self.stack = {"id": 3, "source": self.source}
        self.original_sha = hashlib.sha256(self.baseline).hexdigest()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)
        self.temporary.cleanup()

    def test_partial_authenticated_body_reaches_eof_and_400_preserves_both_readbacks(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
            result = self.owner.interrupt_apply(self.stack, self.candidate, self.original_sha)
        self.assertEqual({"interrupted": True, "status": 400, "readBackExact": True, "baselinePreserved": True}, result)
        self.assertEqual(["GET", "PUT", "GET"], [event["method"] for event in self.events])
        update = self.events[1]
        self.assertGreater(update["declared"], update["received"])
        self.assertEqual(1, update["received"])
        self.assertTrue(update["eofBeforeLength"])
        self.assertTrue(all(event["authorized"] for event in self.events))
        self.assertEqual(self.baseline, self.source.read_bytes())
        self.assertEqual(self.prepared, self.candidate.read_bytes())
        self.assertEqual("", output.getvalue())

    def test_complete_apply_still_sends_the_full_reviewed_candidate_and_safe_flags(self):
        result = self.owner.apply(self.stack, self.candidate, self.original_sha)
        self.assertTrue(result["readBackExact"])
        self.assertEqual(self.prepared, self.source.read_bytes())
        update = next(event for event in self.events if event["method"] == "PUT")
        self.assertEqual(update["declared"], update["received"])
        self.assertFalse(update["eofBeforeLength"])
        self.assertFalse(update["prune"])
        self.assertFalse(update["pull"])

    def test_changed_baseline_stops_before_any_update(self):
        self.source.write_bytes(self.prepared)
        with self.assertRaisesRegex(RuntimeError, "source changed after review"):
            self.owner.interrupt_apply(self.stack, self.candidate, self.original_sha)
        self.assertEqual(["GET"], [event["method"] for event in self.events])

    def test_unknown_response_cannot_count_as_confirmed_interruption(self):
        self.reject_status = 500
        with self.assertRaisesRegex(RuntimeError, "required HTTP 400"):
            self.owner.interrupt_apply(self.stack, self.candidate, self.original_sha)
        self.assertEqual(self.baseline, self.source.read_bytes())

    def test_400_without_preserved_baseline_cannot_count_as_success(self):
        self.change_on_interruption = True
        with self.assertRaisesRegex(RuntimeError, "changed the reviewed baseline"):
            self.owner.interrupt_apply(self.stack, self.candidate, self.original_sha)

    def test_oversized_response_cannot_count_as_confirmed_interruption(self):
        self.response_bytes = b"x" * 16385
        with self.assertRaisesRegex(RuntimeError, "response exceeded"):
            self.owner.interrupt_apply(self.stack, self.candidate, self.original_sha)


if __name__ == "__main__":
    unittest.main(verbosity=2)

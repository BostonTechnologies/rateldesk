#!/usr/bin/env python3
"""Run the repository's full Slopwatch policy without changing its baseline."""

import argparse
from collections import Counter
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
SOLUTION = Path("Helpdesk.sln")
MANIFEST = Path(".config/dotnet-tools.json")
CONFIG = Path(".slopwatch/config.json")
BASELINE = Path(".slopwatch/baseline.json")
VERSION = "0.5.0"
RULES = {"SW001", "SW002", "SW003", "SW004", "SW005", "SW006", "SW021"}
SEVERITIES = {"error", "warning", "info"}


def unique_keys(pairs):
    result = {}
    for name, value in pairs:
        if name in result:
            raise ValueError(f"duplicate JSON key {name!r}")
        result[name] = value
    return result


def read_json(path):
    try:
        return json.loads((ROOT / path).read_text(encoding="utf-8-sig"), object_pairs_hook=unique_keys)
    except (OSError, ValueError) as error:
        raise ValueError(f"{path}: {error}") from error


def validate_policy():
    if not (ROOT / SOLUTION).is_file():
        raise ValueError(f"repository root {ROOT} is missing {SOLUTION}")
    sdk = read_json(Path("global.json"))
    sdk_version = sdk["sdk"]["version"]
    if not isinstance(sdk_version, str) or not re.fullmatch(r"\d+\.\d+\.\d+", sdk_version):
        raise ValueError("global.json must pin a .NET SDK version")

    manifest = read_json(MANIFEST)
    tool = manifest["tools"]["slopwatch.cmd"]
    if (manifest["version"] != 1 or manifest["isRoot"] is not True
            or tool["version"] != VERSION or tool["commands"] != ["slopwatch"]
            or tool.get("rollForward", False) is not False):
        raise ValueError(f"{MANIFEST} must pin the local Slopwatch command to {VERSION}")

    config = read_json(CONFIG)
    if not isinstance(config, dict) or set(config) != {"suppressions", "globalSuppressions", "generatedCode"}:
        raise ValueError(f"{CONFIG} has unsupported or missing policy fields")
    if config["globalSuppressions"] != [] or config["suppressions"] != []:
        raise ValueError(f"{CONFIG} must leave path and global suppressions empty")
    generated = config["generatedCode"]
    if (not isinstance(generated, dict) or set(generated) != {"mode", "detectAttributes"}
            or generated["mode"] not in ("skip", "analyze", "analyzeAndReport")
            or generated["detectAttributes"] is not False):
        raise ValueError(f"{CONFIG} has invalid generated-code settings")

    baseline = read_json(BASELINE)
    if not isinstance(baseline, dict) or type(baseline.get("version")) is not int or baseline["version"] != 2:
        raise ValueError(f"{BASELINE} must use schema v2")
    entries = baseline.get("entries")
    if not isinstance(entries, list):
        raise ValueError(f"{BASELINE} must contain an entries array")
    hashes = set()
    for entry in entries:
        if (not isinstance(entry, dict)
                or not all(isinstance(entry.get(field), str) for field in ("hash", "ruleId", "filePath", "codeSnippet"))
                or not re.fullmatch(r"[0-9a-f]{16}", entry["hash"]) or entry["hash"] in hashes
                or entry["ruleId"] not in RULES or not entry["filePath"]):
            raise ValueError(f"{BASELINE} contains an invalid or duplicate entry")
        hashes.add(entry["hash"])
    return sdk_version, len(entries)


def run(*command):
    return subprocess.run(
        command, cwd=ROOT, capture_output=True, text=True,
        env={**os.environ, "DOTNET_NOLOGO": "1", "DOTNET_CLI_UI_LANGUAGE": "en-US"},
    )


def checked(*command):
    result = run(*command)
    if result.returncode:
        raise ValueError(f"{' '.join(command)} exited {result.returncode}\n{result.stdout}{result.stderr}")
    return result.stdout.strip(), result.stderr.strip()


def read_findings(stdout, stderr, status):
    stats = re.findall(r"^Stats: (\d+) files analyzed in [0-9.,]+s\s*$", stderr, re.MULTILINE)
    if len(stats) != 1 or int(stats[0]) == 0:
        raise ValueError("analysis did not report a nonzero input count")
    report = json.loads(stdout, object_pairs_hook=unique_keys)
    findings = report["results"]
    if (report["version"] != "1.0" or not isinstance(findings, list)
            or report["totalIssues"] != len(findings)
            or status != int(bool(findings))):
        raise ValueError("JSON report and Slopwatch exit status disagree")
    counts = Counter()
    for finding in findings:
        if (finding["ruleId"] not in RULES or finding["severity"] not in SEVERITIES
                or not isinstance(finding["filePath"], str) or not isinstance(finding["message"], str)
                or type(finding["lineNumber"]) is not int):
            raise ValueError("JSON report contains an invalid finding")
        counts[finding["severity"]] += 1
    if report["summary"] != {"errors": counts["error"], "warnings": counts["warning"], "info": counts["info"]}:
        raise ValueError("JSON report has an inconsistent findings summary")
    return int(stats[0]), findings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report-dir", type=Path, default=ROOT / ".slopwatch/reports")
    reports = parser.parse_args().report_dir.resolve()
    reports.mkdir(parents=True, exist_ok=True)
    start = time.monotonic()
    summary = {"toolVersion": VERSION, "files": 0}
    status = 2
    for name in ("findings.json", "analysis.log", "restore.log"):
        (reports / name).unlink(missing_ok=True)
    try:
        sdk_version, summary["baselineEntries"] = validate_policy()
        summary["sourceRevision"], _ = checked("git", "rev-parse", "HEAD")
        worktree, _ = checked("git", "status", "--porcelain")
        summary["worktreeModified"] = bool(worktree)
        summary["sdkVersion"], _ = checked("dotnet", "--version")
        if summary["sdkVersion"] != sdk_version:
            raise ValueError(f"expected SDK {sdk_version}, resolved {summary['sdkVersion']}")

        restore = run("dotnet", "tool", "restore", "--tool-manifest", str(MANIFEST))
        (reports / "restore.log").write_text(restore.stdout + restore.stderr, encoding="utf-8")
        if restore.returncode:
            raise ValueError(f"tool restore exited {restore.returncode}\n{restore.stdout}{restore.stderr}")

        version_stdout, version_stderr = checked("dotnet", "tool", "run", "slopwatch", "--version")
        if not re.search(r"(?:^|\s)0\.5\.0(?:\+[0-9A-Za-z.-]+)?(?:\s|$)", f"{version_stdout} {version_stderr}"):
            raise ValueError(f"expected Slopwatch {VERSION}, got {version_stdout} {version_stderr}")

        analysis = run(
            "dotnet", "tool", "run", "slopwatch", "analyze", "-d", ".",
            "--config", str(CONFIG), "--baseline", str(BASELINE), "--min-severity", "info",
            "--fail-on", "info", "--parallel", "0", "--output", "json", "--stats",
        )
        (reports / "findings.json").write_text(analysis.stdout, encoding="utf-8")
        (reports / "analysis.log").write_text(analysis.stderr, encoding="utf-8")
        if analysis.returncode not in (0, 1):
            raise ValueError(f"analysis exited {analysis.returncode}\n{analysis.stderr}")
        summary["files"], findings = read_findings(analysis.stdout, analysis.stderr, analysis.returncode)
        summary["findings"] = len(findings)
        for finding in findings:
            path = os.path.relpath(finding["filePath"], ROOT)
            print(f"{path}:{finding['lineNumber']}: {finding['ruleId']} [{finding['severity']}] {finding['message']}")
        status = analysis.returncode
    except (OSError, ValueError, KeyError, TypeError) as error:
        summary["error"] = str(error)
        print(f"Slopwatch gate: {error}", file=sys.stderr)
    summary["elapsedSeconds"] = round(time.monotonic() - start, 2)
    summary["exitCode"] = status
    rendered = json.dumps(summary, indent=2)
    (reports / "summary.json").write_text(rendered + "\n", encoding="utf-8")
    print(rendered)
    return status


if __name__ == "__main__":
    sys.exit(main())

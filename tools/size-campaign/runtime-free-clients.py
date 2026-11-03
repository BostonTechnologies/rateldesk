#!/usr/bin/env python3
"""Repeat archived client behavior with no installed shared CoreCLR runtime."""

import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[2]
BASE = "mcr.microsoft.com/dotnet/runtime-deps:10.0.13-noble"


def run(command, **kwargs):
    return subprocess.run(command, check=True, timeout=900, **kwargs)


def windows_roots():
    import winreg
    roots = {Path(value) for key, value in os.environ.items() if key.upper().startswith("DOTNET_ROOT") and value}
    for variable in ("ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA"):
        if value := os.environ.get(variable):
            roots.add(Path(value) / ("Microsoft/dotnet" if variable == "LOCALAPPDATA" else "dotnet"))
    for entry in os.environ.get("PATH", "").split(os.pathsep):
        if (Path(entry) / "dotnet.exe").is_file():
            roots.add(Path(entry))
    for architecture in ("x64", "x86", "arm64"):
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\dotnet\Setup\InstalledVersions" + "\\" + architecture) as key:
                roots.add(Path(winreg.QueryValueEx(key, "InstallLocation")[0]))
        except FileNotFoundError:
            pass
    return sorted({path.resolve() for path in roots if path.is_dir()})


def windows(args, report):
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        raise RuntimeError("Shared-runtime isolation is restricted to disposable GitHub-hosted Windows runners")
    moved = []
    try:
        roots = windows_roots()
        report["runtimeRoots"] = [str(path) for path in roots]
        for root in roots:
            shared = root / "shared"
            if shared.exists():
                hidden = root / ("shared.campaign-isolated-" + uuid.uuid4().hex)
                shared.rename(hidden)
                moved.append((shared, hidden))
        if not moved:
            raise RuntimeError("Expected installed runtime roots to isolate on this runner")
        probe = subprocess.check_output(["dotnet", "--list-runtimes"], text=True, timeout=30)
        if probe.strip() or any((root / "shared").exists() for root in roots):
            raise RuntimeError("A shared runtime remains available")
        report.update(installedRuntimeList=probe, isolation="All discovered shared framework roots detached for the fixture; SDK files remain")
        run([sys.executable, str(ROOT / "tools/size-campaign/client-behavior.py"), "--archives", str(args.archives.resolve()),
             "--rid", args.rid, "--source-revision", args.source_revision, "--output", str(args.output / "behavior")])
        report["behavior"] = json.loads((args.output / "behavior/report.json").read_text())
    finally:
        report["restoration"] = []
        for shared, hidden in reversed(moved):
            hidden.rename(shared)
            report["restoration"].append({"path": str(shared), "status": "RESTORED"})


def linux(args, report):
    image = "rateldesk-runtime-free:" + args.rid
    with tempfile.TemporaryDirectory(prefix="rd-runtime-free-build-") as temporary:
        dockerfile = Path(temporary) / "Dockerfile"
        dockerfile.write_text(f"FROM {BASE}\nRUN apt-get update && apt-get install -y --no-install-recommends python3 openssl && rm -rf /var/lib/apt/lists/*\n")
        run(["docker", "build", "--pull", "--tag", image, temporary])
    report.update(runtimeDepsBase=BASE, image=json.loads(subprocess.check_output(["docker", "image", "inspect", image], text=True))[0]["Id"])
    expected = "aarch64" if args.rid == "linux-arm64" else "x86_64"
    if platform.machine() != expected:
        raise RuntimeError("Native matching Linux hardware required")
    probe = ("import json,pathlib,platform,shutil; "
             "assert shutil.which('dotnet') is None; "
             "hits=[str(p) for root in ['/usr','/opt'] for p in pathlib.Path(root).rglob('*') if p.name in ['libcoreclr.so','libhostfxr.so']]; "
             "assert not hits,hits; "
             f"assert platform.machine()=={expected!r}; "
             "print(json.dumps({'dotnetOnPath':False,'installedCoreclrOrHostfxr':hits,'machine':platform.machine()}))")
    isolation = subprocess.check_output(["docker", "run", "--rm", image, "python3", "-c", probe], text=True, timeout=60)
    report["isolation"] = json.loads(isolation)
    run(["docker", "run", "--rm", "--mount", f"type=bind,src={ROOT},dst=/repository,readonly",
         "--mount", f"type=bind,src={args.archives.resolve()},dst=/archives,readonly",
         "--mount", f"type=bind,src={args.output},dst=/evidence", image, "python3",
         "/repository/tools/size-campaign/client-behavior.py", "--archives", "/archives", "--rid", args.rid,
         "--source-revision", args.source_revision, "--output", "/evidence/behavior"])
    report["behavior"] = json.loads((args.output / "behavior/report.json").read_text())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archives", required=True, type=Path)
    parser.add_argument("--rid", required=True, choices=("linux-x64", "linux-arm64", "win-x64"))
    parser.add_argument("--source-revision", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    report = {"status": "FAIL", "sourceRevision": args.source_revision, "rid": args.rid, "releaseEligible": False}
    try:
        if args.rid == "win-x64":
            if platform.system() != "Windows" or platform.machine().lower() not in ("amd64", "x86_64"):
                raise RuntimeError("Native Windows x64 required")
            windows(args, report)
        else:
            if platform.system() != "Linux":
                raise RuntimeError("Native Linux required")
            linux(args, report)
        behavior = report["behavior"]
        if behavior["status"] != "PASS" or behavior["sourceRevision"] != args.source_revision or behavior["rid"] != args.rid:
            raise RuntimeError("Runtime-free behavior receipt did not pass for the expected source/RID")
        report["status"] = "PASS"
    except Exception as error:
        report["error"] = str(error)
    finally:
        (args.output / "runtime-free.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({"status": report["status"], "rid": args.rid, "error": report.get("error")}))
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())

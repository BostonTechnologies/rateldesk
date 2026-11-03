#!/usr/bin/env python3
"""Check the dependency-only client's published JIT payload, not runtime behavior."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys


APP_NAMES = {"cli": "rateldesk", "stdio": "rateldesk-mcp"}
SUPPORTED_RIDS = ("linux-x64", "linux-arm64", "win-x64")
SHARED_ASSEMBLIES = (
    "Helpdesk.AgentClient.dll", "Helpdesk.Setup.dll", "Helpdesk.Mcp.Core.dll",
    "Helpdesk.Shared.dll", "Termina.dll", "ModelContextProtocol.dll",
    "ModelContextProtocol.Core.dll", "Microsoft.Extensions.Hosting.dll",
    "Microsoft.Extensions.Configuration.Binder.dll", "System.Private.CoreLib.dll",
)


def forbidden_dependency(name: str) -> bool:
    name = name.casefold()
    return name in ("npgsql", "microsoft.aspnetcore") or name.startswith(("npgsql.", "microsoft.aspnetcore."))


def check_publish(directory: Path, target: str, rid: str) -> dict:
    errors: list[str] = []
    report = {
        "schemaVersion": 1,
        "target": target,
        "runtimeIdentifier": rid,
        "scope": "Untrimmed self-contained JIT dependency closure; behavior requires archive acceptance",
        "errors": errors,
    }
    if not directory.is_dir():
        errors.append("Publish directory does not exist")
        return {**report, "status": "FAIL"}
    app = APP_NAMES[target]
    windows = rid == "win-x64"
    native = ("coreclr.dll", "clrjit.dll", "hostfxr.dll", "hostpolicy.dll") if windows else (
        "libcoreclr.so", "libclrjit.so", "libhostfxr.so", "libhostpolicy.so")
    required = (*SHARED_ASSEMBLIES, *native, app + (".exe" if windows else ""),
                app + ".dll", app + ".deps.json", app + ".runtimeconfig.json")
    for name in required:
        path = directory / name
        if not path.is_file() or path.is_symlink():
            errors.append("Required standalone runtime/setup file is missing: " + name)

    files = []
    for path in directory.rglob("*"):
        if path.is_symlink():
            errors.append("Publish payload contains a symbolic link: " + path.relative_to(directory).as_posix())
        elif path.is_file():
            files.append(path)
            if path.suffix.casefold() == ".dll" and forbidden_dependency(path.stem):
                errors.append("Unnecessary framework/database DLL remains: " + path.relative_to(directory).as_posix())
    report["fileCount"] = len(files)
    report["expandedBytes"] = sum(path.stat().st_size for path in files)

    def read_object(name: str) -> dict:
        try:
            value = json.loads((directory / name).read_text(encoding="utf-8-sig"))
        except (OSError, ValueError):
            errors.append("Missing or invalid publish metadata: " + name)
            return {}
        if not isinstance(value, dict):
            errors.append("Publish metadata is not an object: " + name)
            return {}
        return value

    runtime = read_object(app + ".runtimeconfig.json").get("runtimeOptions", {})
    if not isinstance(runtime, dict):
        runtime = {}
    included = runtime.get("includedFrameworks", [])
    frameworks = [item.get("name") for item in included if isinstance(item, dict)] if isinstance(included, list) else []
    if frameworks != ["Microsoft.NETCore.App"]:
        errors.append("Expected only Microsoft.NETCore.App in includedFrameworks")
    if runtime.get("framework") or runtime.get("frameworks"):
        errors.append("Framework-dependent runtime configuration is not a standalone client")
    report["includedFrameworks"] = frameworks

    deps = read_object(app + ".deps.json")
    runtime_target = deps.get("runtimeTarget", {})
    name = runtime_target.get("name", "") if isinstance(runtime_target, dict) else ""
    targets = deps.get("targets", {})
    if not isinstance(name, str) or not name.endswith("/" + rid) or not isinstance(targets, dict) or name not in targets:
        errors.append("Dependency target does not match the selected RID")
    libraries = deps.get("libraries", {})
    if not isinstance(libraries, dict) or not libraries:
        errors.append("Dependency library inventory is missing or empty")
        libraries = {}
    library_names = set(libraries)
    if isinstance(targets, dict):
        for graph in targets.values():
            if isinstance(graph, dict):
                library_names.update(graph)
    forbidden = sorted(key for key in library_names if forbidden_dependency(key.split("/", 1)[0]))
    if forbidden:
        errors.append("Unnecessary framework/database dependencies remain: " + ", ".join(forbidden))
    report["dependencyLibraryCount"] = len(libraries)
    report["status"] = "FAIL" if errors else "PASS"
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--target", choices=APP_NAMES, required=True)
    parser.add_argument("--rid", choices=SUPPORTED_RIDS, required=True)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    result = check_publish(args.directory, args.target, args.rid)
    text = json.dumps(result, indent=2) + "\n"
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(text, encoding="utf-8")
    sys.stdout.write(text)
    return 0 if result["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())

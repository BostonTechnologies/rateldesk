#!/usr/bin/env python3
"""Partition native VSTest discovery without dropping cases; verify its complete receipt union."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def partition(name, count):
    return int.from_bytes(hashlib.sha256(name.encode()).digest()[:4], "big") % count


def receipt(path):
    root = ET.parse(path).getroot()
    results = root.findall("t:Results/t:UnitTestResult", NS)
    counters = root.find("t:ResultSummary/t:Counters", NS)
    if counters is None or not results or int(counters.get("total", "0")) != len(results):
        raise ValueError("Missing or zero-test .NET receipt")
    if int(counters.get("executed", "0")) <= 0 or any(int(counters.get(k, "0")) for k in ["failed", "error", "timeout", "aborted"]):
        raise ValueError(".NET shard did not complete successfully")
    definitions = {x.get("id"): x.find("t:TestMethod", NS) for x in root.findall("t:TestDefinitions/t:UnitTest", NS)}
    names = set()
    for result in results:
        if result.get("outcome") not in ["Passed", "NotExecuted"]:
            raise ValueError("Unexpected .NET result disposition")
        method = definitions.get(result.get("testId"))
        if method is None:
            raise ValueError("Result lacks its discovered method identity")
        names.add(method.get("className") + "." + method.get("name"))
    return names


def run(args):
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    assembly = Path("tests/Helpdesk.Tests/bin") / args.configuration / "net10.0/Helpdesk.Tests.dll"
    discovery = output / "discovery.txt"
    subprocess.run(["dotnet", "vstest", str(assembly), "/ListFullyQualifiedTests", "/ListTestsTargetPath:" + str(discovery)], check=True)
    names = sorted({line.strip() for line in discovery.read_text().splitlines() if line.strip()})
    if not names or any(not name.startswith("Helpdesk.Tests.") for name in names):
        raise ValueError("Unexpected or empty native discovery")
    selected = [name for name in names if partition(name, args.count) == args.index]
    if not selected:
        raise ValueError("Empty .NET shard")
    manifest = {"index": args.index, "count": args.count, "discovered": names, "selected": selected}
    (output / "selection.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(f".NET shard {args.index + 1}/{args.count}: {len(selected)} of {len(names)} discovered methods (all theory rows retained)", flush=True)
    expression = "|".join("FullyQualifiedName=" + name for name in selected)
    subprocess.run(["dotnet", "vstest", str(assembly), "/TestCaseFilter:" + expression,
                    "/Logger:trx;LogFileName=tests.trx", "/ResultsDirectory:" + str(output)], check=True)
    if receipt(output / "tests.trx") != set(selected):
        raise ValueError("Selected .NET methods and executed receipt differ")


def verify(args):
    files = sorted(Path(args.output).rglob("selection.json"))
    if len(files) != args.count:
        raise ValueError("Missing .NET shard evidence")
    discovered = None
    union = set()
    indexes = set()
    for path in files:
        data = json.loads(path.read_text())
        if data["count"] != args.count or data["index"] in indexes:
            raise ValueError("Unexpected or duplicate shard")
        indexes.add(data["index"])
        names = set(data["discovered"])
        if discovered is not None and discovered != names:
            raise ValueError("Shards discovered different source suites")
        discovered = names
        selected = set(data["selected"])
        if selected != {name for name in names if partition(name, args.count) == data["index"]} or union & selected:
            raise ValueError("Shard selection is incomplete or overlaps")
        if receipt(path.with_name("tests.trx")) != selected:
            raise ValueError("Shard receipt does not cover its selection")
        union |= selected
    if indexes != set(range(args.count)) or not discovered or union != discovered:
        raise ValueError("The .NET receipt union omits discovered methods")
    print(f"Complete disjoint .NET coverage: {len(union)} discovered methods across {args.count} passing shards")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["run", "verify"])
    parser.add_argument("--index", type=int, default=0)
    parser.add_argument("--count", type=int, default=6)
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--output", required=True)
    options = parser.parse_args()
    if options.count <= 0 or not 0 <= options.index < options.count:
        parser.error("Invalid shard coordinates")
    try:
        (run if options.mode == "run" else verify)(options)
    except (ValueError, OSError, ET.ParseError, subprocess.CalledProcessError) as error:
        raise SystemExit(str(error)) from error

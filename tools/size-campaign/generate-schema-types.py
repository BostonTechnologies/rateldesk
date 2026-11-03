#!/usr/bin/env python3
"""Regenerate explicit reflection roots from the compiled, closed MCP DTO graph."""

import argparse
import json
from pathlib import Path
import re
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[2]


def render(graph):
    names = [item["name"] for item in graph["objects"]] + graph["enums"]
    if not names or any(not isinstance(name, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.]*", name) for name in names):
        raise ValueError("Schema inventory contains an unsupported C# type name")
    lines = ["// Generated from the compiled MCP schema graph by tools/size-campaign/generate-schema-types.py.",
             "using System.Reflection;", "", "namespace Helpdesk.Mcp.Tools;", "", "internal static class McpSchemaTypes", "{",
             "    private static readonly IReadOnlyDictionary<Type, PropertyInfo[]> Properties = new Dictionary<Type, PropertyInfo[]>", "    {"]
    for item in graph["objects"]:
        name = "global::" + item["name"]
        lines.append(f"        [typeof({name})] = typeof({name}).GetProperties(BindingFlags.Public | BindingFlags.Instance),")
    lines += ["    };", "", "    private static readonly IReadOnlyDictionary<Type, (string[] Names, int[] Numbers)> Enums =",
              "        new Dictionary<Type, (string[] Names, int[] Numbers)>", "        {"]
    for item in graph["enums"]:
        name = "global::" + item
        lines.append(f"            [typeof({name})] = (Enum.GetNames<{name}>(), Enum.GetValues<{name}>().Select(value => Convert.ToInt32(value)).ToArray()),")
    lines += ["        };", "", "    internal static PropertyInfo[] GetProperties(Type type)",
              "        => Properties.TryGetValue(type, out var properties) ? properties",
              '            : throw new InvalidOperationException($"Unregistered compiled MCP schema model: {type.FullName}.");', "",
              "    internal static (string[] Names, int[] Numbers) EnumValues(Type type)",
              "        => Enums.TryGetValue(type, out var values) ? values",
              '            : throw new InvalidOperationException($"Unregistered compiled MCP schema enum: {type.FullName}.");', "}"]
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assembly", type=Path, required=True, help="Core DLL in the owning untrimmed stdio application's complete build output")
    parser.add_argument("--work-directory", type=Path, required=True, help="Fresh directory for the temporary SDK tool and inventory")
    parser.add_argument("--output", type=Path, default=ROOT / "src/Helpdesk.Mcp.Core/Tools/McpSchemaTypes.cs")
    args = parser.parse_args()
    assembly = args.assembly.resolve(strict=True)
    if assembly.name != "Helpdesk.Mcp.Core.dll":
        parser.error("--assembly must identify Helpdesk.Mcp.Core.dll")
    work = args.work_directory.resolve()
    work.mkdir(parents=True, exist_ok=False)
    project = work / "inventory-tool"
    subprocess.run(["dotnet", "new", "console", "--framework", "net10.0", "--no-restore", "--output", str(project)], cwd=ROOT, check=True)
    shutil.copy2(Path(__file__).with_name("schema-model-inventory.cs"), project / "Program.cs")
    graph_path = work / "schema-types.json"
    subprocess.run(["dotnet", "run", "--project", str(project), "-p:UseSharedCompilation=false", "--", str(assembly),
                    str(ROOT / "src/Helpdesk.Mcp.Core/Tools/McpRequestSchemas.cs"), str(graph_path)], cwd=ROOT, check=True)
    graph = json.loads(graph_path.read_text())
    args.output.write_text(render(graph), encoding="utf-8")
    print(f"Wrote {args.output}: {len(graph['objects'])} models and {len(graph['enums'])} enums")


if __name__ == "__main__":
    main()

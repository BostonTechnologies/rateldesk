#!/usr/bin/env python3
"""Fast, offline documentation placement and local-link validation."""

from pathlib import Path
import argparse
import re
import subprocess
import sys
from urllib.parse import unquote, urlsplit


REPO = Path(__file__).resolve().parents[2]
DOCS_ROOT_FILES = {"README.md"}
LINK = re.compile(r"!?\[[^\]\n]*\]\(\s*(<[^>]+>|[^\s)]+)(?:\s+[\"'][^\n]*[\"'])?\s*\)")
REFERENCE = re.compile(r"^\s*\[[^\]]+\]:\s*(<[^>]+>|\S+)", re.MULTILINE)
HTML_LINK = re.compile(r"\b(?:src|href)=[\"']([^\"']+)[\"']", re.IGNORECASE)
HEADING = re.compile(r"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$", re.MULTILINE)
FENCE = re.compile(r"^\s{0,3}(`{3,}|~{3,})")


def prose(text: str) -> str:
    """Ignore code examples while retaining line positions for useful errors."""
    lines, marker = [], None
    for line in text.splitlines(keepends=True):
        match = FENCE.match(line)
        if match:
            token = match.group(1)
            if marker is None:
                marker = token[0]
            elif token[0] == marker:
                marker = None
            lines.append("\n")
        else:
            lines.append(line if marker is None else "\n")
    return "".join(lines)


def anchors(text: str) -> set[str]:
    result, occurrences = set(), {}
    for match in HEADING.finditer(prose(text)):
        heading = re.sub(r"<[^>]+>", "", match.group(1)).lower()
        slug = re.sub(r"[^\w\- ]", "", heading).replace(" ", "-")
        count = occurrences.get(slug, 0)
        occurrences[slug] = count + 1
        result.add(f"{slug}-{count}" if count else slug)
    result.update(re.findall(r"\b(?:id|name)=[\"']([^\"']+)[\"']", text))
    return result


def exact_path(path: Path) -> bool:
    """Check each spelling explicitly even on case-insensitive developer hosts."""
    try:
        relative = path.relative_to(REPO)
    except ValueError:
        return False
    current = REPO
    for part in relative.parts:
        if not current.is_dir() or part not in {child.name for child in current.iterdir()}:
            return False
        current /= part
    return current.exists()


def main() -> int:
    global REPO
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, help="Validate an extracted release documentation tree instead of the Git checkout.")
    args = parser.parse_args()
    if args.root is not None:
        REPO = args.root.resolve()
    failures = []
    for path in (REPO / "docs").iterdir():
        if path.is_file() and path.name not in DOCS_ROOT_FILES:
            failures.append(f"docs-root file must be categorized: {path.relative_to(REPO)}")
    if not (REPO / "docs/README.md").is_file():
        failures.append("missing docs/README.md")
    if not (REPO / "AGENTS.md").is_file():
        failures.append("missing canonical uppercase AGENTS.md")

    # Include untracked guides so a local check catches new files before staging.
    if args.root is not None:
        markdown = sorted(REPO.rglob("*.md"))
    else:
        paths = subprocess.check_output(
            ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
            cwd=REPO,
        ).decode().split("\0")
        markdown = sorted({REPO / p for p in paths if p.endswith(".md") and (REPO / p).is_file() and not p.startswith("work/")})
    checked = 0
    for path in markdown:
        text = prose(path.read_text(encoding="utf-8"))
        for pattern in (LINK, REFERENCE, HTML_LINK):
            for match in pattern.finditer(text):
                raw = match.group(1).strip("<>")
                url = urlsplit(raw)
                # Leading-slash links name application routes, not checkout files.
                if url.scheme or url.netloc or raw.startswith("/"):
                    continue
                checked += 1
                target = (path.parent / unquote(url.path)).resolve() if url.path else path
                line = text.count("\n", 0, match.start()) + 1
                location = f"{path.relative_to(REPO)}:{line}"
                if not exact_path(target):
                    failures.append(f"{location}: missing or case-mismatched local target {raw}")
                elif url.fragment and target.suffix.lower() == ".md" and unquote(url.fragment) not in anchors(target.read_text(encoding="utf-8")):
                    failures.append(f"{location}: missing local heading {raw}")
    if failures:
        for failure in failures:
            print(f"documentation validation: {failure}", file=sys.stderr)
        return 1
    print(f"documentation validation passed ({len(markdown)} Markdown files, {checked} local links; docs-root allowlist: README.md)")
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Create one new prerelease tag from current main after verified main CI.

Run only from the release workflow's explicitly requested preparation job.
This creates no release, uploads no asset and never updates an existing ref.
The same workflow continues through the normal release validation and staging.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request


REQUIRED_JOBS = {
    "Public disclosure gate",
    ".NET validation",
    "Incident receiver HTTP and provider acceptance",
    "External PostgreSQL Compose configuration",
    "Layout and reference validation",
}
IMAGE_NAMES = ("rateldesk-api", "rateldesk-web", "rateldesk-mcp-http")
TAG = re.compile(r"^(?:v|RatelDesk-)((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)-(?:beta|rc)\.(?:0|[1-9][0-9]*))$")


def fail(message: str) -> None:
    raise SystemExit(message)


def validate_request(environment: dict[str, str], evaluated_version: str) -> tuple[str, str, str]:
    tag = environment.get("RELEASE_TAG", "")
    source = environment.get("RELEASE_SOURCE_REVISION", "")
    repository = environment.get("GITHUB_REPOSITORY", "")
    match = TAG.fullmatch(tag)
    if match is None or match.group(1) != evaluated_version:
        fail("Tag creation requires a beta or RC matching the unmodified repository version.")
    if not re.fullmatch(r"[0-9a-f]{40}", source):
        fail("Tag creation requires the full source commit SHA.")
    if (environment.get("GITHUB_EVENT_NAME") != "workflow_dispatch"
            or environment.get("GITHUB_REF") != "refs/heads/main"
            or environment.get("GITHUB_SHA") != source):
        fail("Tag creation must be explicitly dispatched against the selected main source.")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        fail("The workflow repository is invalid.")
    return tag, source, repository


def validate_main_ci(source: str, runs: list[dict], jobs: list[dict]) -> dict:
    matching = [run for run in runs if run.get("head_sha") == source
                and run.get("head_branch") == "main" and run.get("event") == "push"]
    if not matching:
        fail("No normal main CI run exists for the requested source.")
    run = max(matching, key=lambda item: item["id"])
    if run.get("status") != "completed" or run.get("conclusion") != "success":
        fail("The latest normal main CI run must finish successfully before tagging.")
    for name in REQUIRED_JOBS:
        selected = [job for job in jobs if job.get("name") == name]
        if len(selected) != 1 or selected[0].get("status") != "completed" or selected[0].get("conclusion") != "success":
            fail(f"Required exact-source main check is missing, skipped or unsuccessful: {name}")
        if selected[0].get("head_sha") != source or selected[0].get("run_id") != run["id"]:
            fail(f"Required main check is bound to a different run or source: {name}")
    return run


def request_json(url: str, headers: dict[str, str] | None = None, body: dict | None = None, allow_missing: bool = False):
    payload = None if body is None else json.dumps(body).encode("utf-8")
    request = urllib.request.Request(url, data=payload, headers=headers or {}, method="GET" if body is None else "POST")
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        if allow_missing and error.code == 404:
            return None
        # Do not print response bodies or authentication headers into release logs.
        fail(f"Release preflight request failed with HTTP {error.code}.")


def require_unused_images(repository: str, version: str, image_names=IMAGE_NAMES) -> None:
    owner = repository.split("/", 1)[0].lower()
    accept = ", ".join(("application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json",
                        "application/vnd.oci.image.manifest.v1+json", "application/vnd.docker.distribution.manifest.v2+json"))
    for name in image_names:
        if name not in IMAGE_NAMES:
            fail("Unknown release image family.")
        scope = urllib.parse.urlencode({"service": "ghcr.io", "scope": f"repository:{owner}/{name}:pull"})
        token = request_json(f"https://ghcr.io/token?{scope}")
        bearer = token.get("token") or token.get("access_token")
        if not isinstance(bearer, str) or not bearer:
            fail("Anonymous registry allocation could not be verified.")
        manifest = request_json(f"https://ghcr.io/v2/{owner}/{name}/manifests/{version}",
                                {"Authorization": f"Bearer {bearer}", "Accept": accept}, allow_missing=True)
        if manifest is not None:
            fail(f"The immutable version already has a registry manifest: {name}.")


def main() -> None:
    evaluated = subprocess.run(
        ["dotnet", "msbuild", "src/Helpdesk.API/Helpdesk.API.csproj", "-nologo", "-getProperty:Version"],
        check=True, capture_output=True, text=True).stdout.strip()
    tag, source, repository = validate_request(dict(os.environ), evaluated)
    token = os.environ.get("GH_TOKEN", "")
    if not token:
        fail("The workflow repository token is required.")
    headers = {"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
               "X-GitHub-Api-Version": "2022-11-28", "Content-Type": "application/json"}
    base = f"https://api.github.com/repos/{repository}"

    def api(path: str, **kwargs):
        return request_json(base + path, headers, **kwargs)

    def pages(path: str, key: str | None = None) -> list:
        result = []
        for page in range(1, 51):
            payload = api(path + ("&" if "?" in path else "?") + f"per_page=100&page={page}")
            batch = payload if key is None else payload[key]
            result.extend(batch)
            if len(batch) < 100:
                return result
        fail("Release preflight exceeded its bounded pagination limit.")

    main_ref = api("/git/ref/heads/main")
    if main_ref["object"]["sha"] != source:
        fail("Main advanced after dispatch; select its newly verified revision.")
    runs = pages("/actions/workflows/pull-request-validation.yml/runs?event=push&branch=main&head_sha=" + source, "workflow_runs")
    matching = [run for run in runs if run.get("head_sha") == source and run.get("event") == "push" and run.get("head_branch") == "main"]
    if not matching:
        fail("No main CI run exists for the requested source.")
    run = max(matching, key=lambda item: item["id"])
    jobs = pages(f"/actions/runs/{run['id']}/jobs?filter=latest", "jobs")
    validate_main_ci(source, runs, jobs)

    aliases = {"v" + evaluated, "RatelDesk-" + evaluated}
    for alias in aliases:
        if api("/git/ref/tags/" + urllib.parse.quote(alias, safe=""), allow_missing=True) is not None:
            fail("This prerelease version already has a tag; existing tags are immutable.")
    if any(release.get("tag_name") in aliases for release in pages("/releases")):
        fail("This prerelease version already has a draft or published release.")
    require_unused_images(repository, evaluated)
    if api("/git/ref/heads/main")["object"]["sha"] != source:
        fail("Main advanced during preflight; no tag was created.")
    created = api("/git/refs", body={"ref": "refs/tags/" + tag, "sha": source})
    if created.get("ref") != "refs/tags/" + tag or created.get("object", {}).get("sha") != source:
        fail("The tag creation receipt does not match the requested source.")
    print(f"Created {tag} at {source} after main CI run {run['id']}. Normal release validation follows.")


if __name__ == "__main__":
    if sys.argv[1:] == ["check-images"]:
        release_repository = os.environ.get("GITHUB_REPOSITORY", "")
        release_version = os.environ.get("RELEASE_VERSION", "")
        if (not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", release_repository)
                or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?", release_version)):
            fail("Exact release repository and version are required for the immutable image check.")
        release_image = os.environ.get("RELEASE_IMAGE_NAME")
        require_unused_images(release_repository, release_version, (release_image,) if release_image else IMAGE_NAMES)
        print("Requested immutable release image tags are unallocated.")
    elif sys.argv[1:]:
        fail("usage: create-prerelease-tag.py [check-images]")
    else:
        main()

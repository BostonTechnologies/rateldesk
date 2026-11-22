"""Download selected public RatelDesk release archives and verify their published identity."""
import hashlib
import json
import os
import re
import subprocess
from pathlib import Path

repo = "BostonTechnologies/rateldesk"
tag = os.environ["RELEASE_TAG"]
sha = os.environ["SOURCE_SHA"]
rid = os.environ["RELEASE_RID"]
if not re.fullmatch(r"v\d+\.\d+\.\d+-beta\.\d+", tag) or not re.fullmatch(r"[0-9a-f]{40}", sha):
    raise ValueError("Invalid release coordinates")
rids = {"linux-x64": "tar.gz", "linux-arm64": "tar.gz", "win-x64": "zip"}
if rid not in rids:
    raise ValueError("Unsupported RID")
version = tag[1:]
out = Path("release-assets")
out.mkdir()
base = f"https://github.com/{repo}/releases/download/{tag}"


def download(url, target):
    subprocess.run(["curl", "-q", "--proto", "=https", "--proto-redir", "=https",
                    "--fail", "--silent", "--show-error", "--location", "--retry", "3",
                    "--output", str(target), url], check=True)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def checksum_map(path):
    values = {}
    for line in path.read_text().splitlines():
        match = re.fullmatch(r"([0-9a-f]{64}) +\*?([A-Za-z0-9._-]+)", line)
        if not match or match[2] in values:
            raise ValueError(f"Invalid or duplicate checksum entry in {path.name}")
        values[match[2]] = match[1]
    return values


download(f"https://api.github.com/repos/{repo}/releases/tags/{tag}", out / "metadata.json")
metadata = json.loads((out / "metadata.json").read_text())
assert metadata["tag_name"] == tag and metadata["prerelease"] and not metadata["draft"]
archives = {f"{tool}-{version}-{platform}.{extension}"
            for platform, extension in rids.items() for tool in ["rateldesk-cli", "rateldesk-mcp-stdio"]}
deployment = f"rateldesk-deployment-{version}.tar.gz"
archives.add(deployment)
controls = {"release-manifest.json", "release-manifest.json.sha256", "SHA256SUMS"}
assets = {asset["name"]: asset for asset in metadata["assets"]}
assert len(assets) == len(metadata["assets"]) and set(assets) == archives | controls
verified = {}


def fetch(name):
    asset = assets[name]
    url = f"{base}/{name}"
    assert asset["browser_download_url"] == url and asset["state"] == "uploaded"
    file = out / name
    download(url, file)
    sha256 = digest(file)
    assert asset["digest"] == f"sha256:{sha256}" and asset["size"] == file.stat().st_size, name
    verified[name] = sha256


for name in sorted(controls):
    fetch(name)
assert checksum_map(out / "release-manifest.json.sha256") == {"release-manifest.json": verified["release-manifest.json"]}
manifest = json.loads((out / "release-manifest.json").read_text())
assert manifest["version"] == version and manifest["sourceRevision"] == sha
checksums = checksum_map(out / "SHA256SUMS")
assert set(manifest["supportedRids"]) == set(rids)
assert set(checksums) == archives and checksums == manifest["assets"]
for name in sorted(archives):
    assert assets[name]["digest"] == "sha256:" + checksums[name]
    if name.endswith(f"-{rid}.{rids[rid]}") or (name == deployment and rid == "linux-x64"):
        fetch(name)
        assert verified[name] == checksums[name], name
receipt = {"tag": tag, "sourceRevision": sha, "rid": rid, "releaseId": metadata["id"], "verifiedSha256": verified}
print(json.dumps(receipt, indent=2))
Path("verified-bytes.json").write_text(json.dumps(receipt, indent=2) + "\n")

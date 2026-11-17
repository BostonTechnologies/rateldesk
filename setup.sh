#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"
sdk_version="$(python3 -c 'import json; print(json.load(open("global.json"))["sdk"]["version"])')"
echo "Updating system and installing .NET SDK $sdk_version"
sudo apt-get update -qq
sudo apt-get install -y -qq wget unzip python3

# Install .NET SDK 10 via official script
wget -q https://dotnet.microsoft.com/download/dotnet/scripts/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x dotnet-install.sh

./dotnet-install.sh --version "$sdk_version"

# Where the script put it
DOTNET_ROOT="${HOME}/.dotnet"
export DOTNET_ROOT
export PATH="${DOTNET_ROOT}:${PATH}"
export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export NUGET_PACKAGES="$HOME/.nuget/packages"  # speeds up restores

# Make dotnet available to subsequent shells in this environment
# 1) Global symlink (preferred in CI/containers)
if [[ -x "${DOTNET_ROOT}/dotnet" ]]; then
  sudo ln -sf "${DOTNET_ROOT}/dotnet" /usr/local/bin/dotnet
fi

# 2) Also persist for interactive shells just in case
PROFILE_SNIPPET='export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"'
if ! grep -q 'DOTNET_ROOT' "${HOME}/.bashrc" 2>/dev/null; then
  echo "${PROFILE_SNIPPET}" >> "${HOME}/.bashrc"
fi
if [ -f "${HOME}/.profile" ] && ! grep -q 'DOTNET_ROOT' "${HOME}/.profile"; then
  echo "${PROFILE_SNIPPET}" >> "${HOME}/.profile"
fi

# Verify installation (this should work both inside this script and in the next step)
dotnet --info
dotnet tool restore --tool-manifest .config/dotnet-tools.json

# JS deps for repo (optional)
if [ -f package.json ]; then
  npm install
fi

echo "Setup complete."

#!/usr/bin/env bash
# Restores every project of the UI evaluation and (re)writes its packages.lock.json.
# Usage: scripts/restore-all.sh [--locked-mode]   (CI uses --locked-mode: the lock files must already match)
set -euo pipefail
cd "$(dirname "$0")/.."
export AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
for p in src/Proto.Views src/Proto.Desktop src/Proto.Render tools/UiEval.Harness tools/UiEval.StartupHook; do
  dotnet restore "$p" "$@"
done

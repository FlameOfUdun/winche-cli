#!/usr/bin/env bash
# Builds the TypeScript SDKs (@winche/functions and @winche/admin, their own repositories) and copies their packages
# into the tests, which put them in the functions folders they make (as a project does before they're on npm): the tests don't
# need npm. Run it after changing an SDK, then commit what it changed here.
#
#   scripts/update-test-packages.sh [path to the TypeScript SDKs' folder]   (default: ../../TypeScript)
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
sdks="$(cd "${1:-$here/../../TypeScript}" && pwd)"
out="$here/tests/Winche.Cli.Tests/SdkPackages/@winche"
for repo in winche-admin-typescript-sdk winche-functions-typescript-sdk; do
  (cd "$sdks/$repo" && { [ -d node_modules ] || npm install --silent; } && npm run build --silent >/dev/null)
  name="$(sed -n 's/.*"name": *"@winche\/\([^"]*\)".*/\1/p' "$sdks/$repo/package.json" | head -1)"
  rm -rf "$out/$name" && mkdir -p "$out/$name"
  cp -R "$sdks/$repo/dist" "$sdks/$repo/package.json" "$out/$name/"
  echo "@winche/$name $(git -C "$sdks/$repo" rev-parse --short HEAD) → tests/Winche.Cli.Tests/SdkPackages/@winche/$name"
done

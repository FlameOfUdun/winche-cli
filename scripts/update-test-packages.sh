#!/usr/bin/env bash
# Builds the TypeScript SDKs (@winche-technologies/winche-functions and @winche-technologies/winche-admin, their own
# repositories) and copies their packages into the tests, which the test host provides to deployments
# (FunctionsOptions.ProvidedPackages): the tests don't need npm. Run it after changing an SDK, then commit what it
# changed here.
#
#   scripts/update-test-packages.sh [path to the TypeScript SDKs' folder]   (default: ../../TypeScript)
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
sdks="$(cd "${1:-$here/../../TypeScript}" && pwd)"
rel="tests/Winche.Cli.Tests/SdkPackages/@winche-technologies"
out="$here/$rel"
for repo in winche-admin-typescript-sdk winche-functions-typescript-sdk; do
  (cd "$sdks/$repo" && { [ -d node_modules ] || npm install --silent; } && npm run build --silent >/dev/null)
  name="${repo%-typescript-sdk}"   # winche-admin, winche-functions
  rm -rf "$out/$name" && mkdir -p "$out/$name"
  cp -R "$sdks/$repo/dist" "$sdks/$repo/package.json" "$out/$name/"
  echo "@winche-technologies/$name $(git -C "$sdks/$repo" rev-parse --short HEAD) → $rel/$name"
done

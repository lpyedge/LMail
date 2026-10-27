#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$ROOT"

if [[ "$ROOT" == "/" || ! -f "$ROOT/LMail.sln" ]]; then
  echo "Refusing to clean outside the LMail workspace." >&2
  exit 1
fi

for path in "$ROOT/temp" "$ROOT/temp/build" "$ROOT/temp/build/dotnet"; do
  if [ -L "$path" ]; then
    echo "Refusing to clean a symlinked build path: $path" >&2
    exit 1
  fi
done

rm -rf -- "$ROOT/temp/build/dotnet"
echo "Removed temp/build/dotnet; preserved release packages, dependencies and logs."

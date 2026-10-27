#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

"$ROOT/scripts/test.sh" "$@"
"$ROOT/scripts/check-structure.sh"
dotnet pack src/packages/LMail/LMail.csproj -c Release --no-build --nologo -o dist/packages "$@"

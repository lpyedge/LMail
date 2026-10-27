#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

dotnet build LMail.sln -c Release --nologo "$@"
dotnet run --project src/samples/LMail.Samples -c Release --no-build -- --self-test

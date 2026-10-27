#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

failures=0

fail() {
  echo "FAIL: $*"
  failures=$((failures + 1))
}

for entry in "$ROOT"/* "$ROOT"/.[!.]*; do
  [ -e "$entry" ] || continue

  name="$(basename "$entry")"

  case "$name" in
    .git|.github|.vscode|.gitignore|.gitattributes|.env.example) ;;
    Directory.Build.props|LMail.sln|LICENSE|NuGet.Config|README.md|global.json) ;;
    dist|docs|plan|scripts|src|temp) ;;
    *) fail "unexpected root entry: $name" ;;
  esac
done

for required in \
  README.md \
  LICENSE \
  Directory.Build.props \
  NuGet.Config \
  global.json \
  LMail.sln \
  src/packages/LMail/LMail.csproj \
  src/samples/LMail.Samples/LMail.Samples.csproj \
  docs/ARCHITECTURE.md \
  scripts/setup.sh \
  scripts/build.sh \
  scripts/test.sh \
  scripts/publish.sh \
  scripts/clean.sh \
  scripts/check-structure.sh; do
  [ -e "$ROOT/$required" ] || fail "missing required path: $required"
done

while IFS= read -r directory; do
  fail "generated build directory outside temp: ${directory#"$ROOT"/}"
done < <(find src docs plan scripts -type d \( -name bin -o -name obj \) -print)

while IFS= read -r package; do
  case "$package" in
    "$ROOT"/dist/packages/*) ;;
    *) fail "package outside dist/packages: ${package#"$ROOT"/}" ;;
  esac
done < <(find "$ROOT" -path "$ROOT/temp" -prune -o -type f \( -name '*.nupkg' -o -name '*.snupkg' \) -print)

while IFS= read -r script; do
  [ -x "$script" ] || fail "script is not executable: ${script#"$ROOT"/}"
done < <(find scripts -type f -name '*.sh' -print)

if [ "$failures" -ne 0 ]; then
  echo
  echo "Structure check failed with $failures issue(s)."
  exit 1
fi

echo "Structure check passed."

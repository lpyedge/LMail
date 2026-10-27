#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

version="$(dotnet msbuild src/packages/LMail/LMail.csproj -nologo -getProperty:Version)"

if [[ "${GITHUB_EVENT_NAME:-}" == "release" ]]; then
  if [[ "${RELEASE_TAG#v}" != "$version" ]]; then
    echo "Release tag '$RELEASE_TAG' must match project version '$version' (optional v prefix)." >&2
    exit 1
  fi
  if [[ "${RELEASE_PRERELEASE:-false}" == "true" && "$version" != *-* ]]; then
    echo "A prerelease needs a prerelease project version, for example 0.1.0-rc.1." >&2
    exit 1
  fi
elif [[ "${GITHUB_EVENT_NAME:-}" == "workflow_dispatch" ]]; then
  if [[ "${PREVIEW:-true}" == "true" ]]; then
    if [[ "$version" == *-* ]]; then
      echo "Use a stable project version as the base for a manual preview." >&2
      exit 1
    fi
    version="${version}-preview.${GITHUB_RUN_NUMBER:?}"
  fi
else
  echo "Version selection requires a release or workflow_dispatch event." >&2
  exit 1
fi

echo "Package version: $version"
echo "PACKAGE_VERSION=$version" >> "${GITHUB_ENV:?}"

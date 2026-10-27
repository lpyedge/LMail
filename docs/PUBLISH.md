# NuGet publishing

The workflow follows [LNotification's release/manual workflow](https://github.com/lpyedge/LNotification/blob/main/.github/workflows/publish.yml).
It installs the SDK from `global.json`, builds the solution, runs the offline sample checks,
checks the workspace structure, packs the library, uploads both package artifacts, then pushes
to NuGet.org. CI performs the same validation and packaging for main pushes and pull requests.

## Setup

Create a NuGet.org API key scoped to pushing `LMail`, and add it to the GitHub repository's
Actions secrets as `NUGET_API_KEY`. The NuGet.org account must own or be allowed to reserve
the package ID. No credentials belong in source files.

Actions supplies the package repository/project URL from `GITHUB_SERVER_URL` and
`GITHUB_REPOSITORY`, so it follows the actual hosting repository. Local packaging can set
`-p:RepositoryUrl=https://github.com/OWNER/REPOSITORY` when that metadata is needed.

## Release

1. Set `<Version>` in `src/packages/LMail/LMail.csproj` to the intended package version.
2. Commit/push the changes and create/push the matching tag manually.
3. Publish a GitHub Release for that tag. `v0.1.0` and `0.1.0` both match version `0.1.0`.

A tag/version mismatch fails before packaging. A GitHub prerelease must use a prerelease
project version such as `0.1.0-rc.1`, so it cannot accidentally publish a stable package.

## Manual publish

Run **Publish NuGet Package** from GitHub Actions on the desired ref.

- `preview: true` (default): stable base `0.1.0` becomes `0.1.0-preview.<run number>`.
  A new run gets a new version; retrying the same run keeps its version.
- `preview: false`: publishes the exact project version, including any prerelease suffix.

NuGet versions are immutable. A duplicate is skipped; bump the version for changed contents.
The workflow pushes the exact package it just built. `dotnet nuget push` also discovers and
pushes its sibling `.snupkg` to NuGet.org's symbol service. Both files are uploaded to the
Actions run before pushing, so they remain available if registry authentication fails.

## Local verification

From the repository root (Bash required):

```bash
bash scripts/setup.sh
bash scripts/publish.sh
bash scripts/publish.sh -p:Version=0.1.0-preview.1
```

`publish.sh` validates before packing; it does not upload packages. Final artifacts live under
`dist/packages/`, while build outputs and dependencies stay under `temp/`.
`bash scripts/clean.sh` removes only `temp/build/dotnet`; it preserves packages, dependencies
and logs, and rejects symlinked cleanup paths.

Live provider sends, real OAuth2 consent, GitHub-hosted execution and registry publishing
require their respective credentials/environment and are not covered by offline checks.

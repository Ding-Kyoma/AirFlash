# Releasing AirFlash

Stable releases are built from `main` by the manually dispatched **Release** GitHub Actions workflow. It installs and validates the selected .NET SDK before reserving a version, then runs native/.NET/Python checks, builds fresh Windows x64 binaries, verifies versions and checksums, exercises MSI installation in a disposable runner, and publishes the release only after success.

Desktop tray verification is documented in [TRAY-DIAGNOSTICS.md](TRAY-DIAGNOSTICS.md).

Versions are three numeric components. `reserved/X.Y.Z` tags are permanent reservations, including failed builds. Never remove, move, or reuse them. The pre-GitHub floor is 0.2.3. `vX.Y.Z` identifies a published release and points to its source commit. Concurrent workflow runs are serialized; atomic remote reservations also prevent local collisions.

A local release build must run from clean `main`, with the authenticated `origin` set to this repository. Run `pwsh scripts/build.ps1`; it reserves its own increasing version remotely. Preserve `artifacts/release-version.txt`. Local builds are validation builds; dispatch the workflow for public distribution. Never pass an old reservation to rebuild a published version.

The workflow's optional `version` input reserves an explicit new version, for example `gh workflow run release.yml --ref main -f version=0.3.0`. Leave it empty to increment automatically. Local validation builds also accept `pwsh scripts/build.ps1 -NewVersion X.Y.Z`. Explicit versions must exceed every remote reservation, published stable version, local version record, existing EXE version, and source baseline, and fit MSI limits (255.255.65535). `-NewVersion` cannot be combined with `-ReservedVersion`; the latter is internal to the Actions job that just reserved the version. Failed builds consume their version. Never retry a failed build with the same explicit version, even on the same commit.

When the release commit modifies workflows, use a maintainer login with workflow-write permission for publication. Dispatch `gh workflow run release.yml --ref main -f version=0.3.0 -f publish=false`. The workflow performs the same build, version, checksum, and installer lifecycle checks, then uploads `airflash-release-<version>-<sha>` without publishing. Download this artifact only from a successful run, verify both file hashes against `SHA256SUMS.txt`, and confirm the run SHA matches `main` and `reserved/<version>`. Publish those exact files with `gh release create v<version> AirFlash.exe AirFlash-X.Y.Z.msi SHA256SUMS.txt --target <verified-sha> --title "AirFlash <version>" --notes-file docs/RELEASE-NOTES.md --latest`. Confirm the release is not a prerelease, is Latest, has all three assets, and its tag points to the verified SHA. The `publish` input defaults to `true`, preserving automatic stable publication. Preview runs always retain their existing artifact-only behavior.

Complete local validation builds before reserving the intended public version, so that the workflow can reserve it as a fresh version.

For local SDK/cache reuse use the checkout's ignored `artifacts` directory or the shared Git integration checkout. `DOTNET_CLI_HOME` and `NUGET_PACKAGES` can override cache locations. Install the .NET 10 SDK, Rust MSVC toolchain and Visual Studio C++ tools before building.

Run `pwsh scripts/check-dotnet.ps1` to check the selected host without starting a release build. Selection prefers the checkout's `artifacts/dotnet/dotnet.exe`, then the integration checkout's equivalent, then `dotnet` on PATH. The selected host resolves `desktop/global.json` from `desktop`, allowing its configured SDK roll-forward. A runtime-only or incompatible preferred installation fails with its path and policy; replace or remove that installation rather than expecting fallback to PATH. `scripts/build.ps1` performs this check before the release lock, reservation, output cleanup or native work, and keeps the same selected host for desktop publish and MSI build. `scripts/dotnet.ps1 --info`, `--list-sdks` and `--list-runtimes` remain available without a compatible SDK. Later build failures still consume a reserved version.

Release assets are `AirFlash.exe`, `AirFlash-X.Y.Z.msi`, and `SHA256SUMS.txt`. Validate a downloaded asset with `Get-FileHash -Algorithm SHA256`. The binaries are currently unsigned.

Installer lifecycle tests run only in the disposable GitHub runner. First release tests use a lower-version fixture built from the same source to exercise upgrades; subsequent releases additionally use the preceding public MSI. They do not interact with HomePods.

Local agent instructions, worktree hooks and history backups are deliberately not distributed. They live only in the maintainer's Git common directory and ignored files.

## Single-NIC discovery preview

Keep the feature on `codex/nic-discovery-preview` and open a PR against `main`; do not merge until users validate it. Dispatch the existing **Release** workflow with branch `codex/nic-discovery-preview` (`gh workflow run release.yml --ref codex/nic-discovery-preview`). The selected branch is the explicit preview channel; `main` remains the stable channel. The preview branch's workflow reserves the next numeric version (including any previous preview reservations), runs the same tests, builds and verifies the EXE/MSI, checks installer lifecycle in a disposable runner, and uploads an artifact named `airflash-preview-<version>-<sha>`. A failed attempt consumes its reserved version; fix the branch and dispatch again for a new one.

The workflow deliberately does not create a GitHub Release for the preview. The branch changes `.github/workflows/release.yml`, so GitHub's ordinary Actions token cannot create a release targeting that commit without workflow-write permission. The maintainer downloads the verified artifact from the successful run, verifies `SHA256SUMS.txt` against both files, confirms the run SHA matches the current PR head and `reserved/<version>` tag, then publishes with a maintainer `gh` login authorized for workflow changes:

`gh release create v<version>-rc.1 AirFlash.exe AirFlash-X.Y.Z.msi SHA256SUMS.txt --target <verified-sha> --title "AirFlash <version> Preview" --notes-file docs/PREVIEW-NOTES.md --prerelease --latest=false`

Verify the resulting release is marked prerelease, is not Latest, has the three assets, and its tag points to the verified SHA. The installed application displays **Preview**; its MSI ProductVersion and EXE file version are numeric `<version>`. The About update check still queries only the latest stable release. After validation, merge the PR and dispatch the stable workflow from `main`; it reserves a *new* higher numeric version. Never repurpose the preview tag or version. Test upgrading the preview MSI to that stable release in a disposable runner.

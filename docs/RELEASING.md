# Releasing AirFlash

Releases are built from `main` by the manually dispatched **Release** GitHub Actions workflow. It reserves a version before tests, runs native/.NET/Python checks, builds fresh Windows x64 binaries, verifies versions and checksums, exercises MSI installation in a disposable runner, and publishes the release only after success.

Versions are three numeric components. `reserved/X.Y.Z` tags are permanent reservations, including failed builds. Never remove, move, or reuse them. The pre-GitHub floor is 0.2.3. `vX.Y.Z` identifies a published release and points to its source commit. Concurrent workflow runs are serialized; atomic remote reservations also prevent local collisions.

A local release build must run from clean `main`, with the authenticated `origin` set to this repository. Run `pwsh scripts/build.ps1`; it reserves its own increasing version remotely. Preserve `artifacts/release-version.txt`. Local builds are validation builds; dispatch the workflow for public distribution. Never pass an old reservation to rebuild a published version.

For local SDK/cache reuse use the checkout's ignored `artifacts` directory or the shared Git integration checkout. `DOTNET_CLI_HOME` and `NUGET_PACKAGES` can override cache locations. Install the .NET 10 SDK, Rust MSVC toolchain and Visual Studio C++ tools before building.

Release assets are `AirFlash.exe`, `AirFlash.msi`, and `SHA256SUMS.txt`. Validate a downloaded asset with `Get-FileHash -Algorithm SHA256`. The binaries are currently unsigned.

Installer lifecycle tests run only in the disposable GitHub runner. First release tests use a lower-version fixture built from the same source to exercise upgrades; subsequent releases additionally use the preceding public MSI. They do not interact with HomePods.

Local agent instructions, worktree hooks and history backups are deliberately not distributed. They live only in the maintainer's Git common directory and ignored files.

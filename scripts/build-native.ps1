param([switch]$Check)
$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cargoCommand = Get-Command cargo -ErrorAction SilentlyContinue
$cargoPath = if ($cargoCommand) { $cargoCommand.Source } else { Join-Path $env:USERPROFILE '.cargo/bin/cargo.exe' }
if (-not (Test-Path -LiteralPath $cargoPath)) { throw 'Rust toolchain missing. Install Rust MSVC via rustup.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'MSVC Build Tools and Windows SDK are required.' }
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Install the Visual Studio Desktop development with C++ workload.' }
$manifest = Join-Path $repoRoot 'native/airflash-engine/Cargo.toml'
# Keep machine/user paths out of native panic locations and debug metadata.
$oldRustFlags = $env:CARGO_ENCODED_RUSTFLAGS
$flags = @('-C', 'target-feature=+crt-static', '--remap-path-prefix', "$repoRoot=/_/", '--remap-path-prefix', "$env:USERPROFILE=/builder")
$env:CARGO_ENCODED_RUSTFLAGS = $flags -join [char]31
Push-Location -LiteralPath $repoRoot
try {
    if ($Check) {
        & $cargoPath test --manifest-path $manifest --locked
        if ($LASTEXITCODE -ne 0) { throw 'Rust tests failed' }
        & $cargoPath clippy --manifest-path $manifest --all-targets --locked -- -D warnings
        if ($LASTEXITCODE -ne 0) { throw 'Rust lint failed' }
    }
    & $cargoPath build --manifest-path $manifest --release --locked
    if ($LASTEXITCODE -ne 0) { throw 'Native engine build failed' }
    $engine = Join-Path $repoRoot 'native/airflash-engine/target/release/airflash-engine.exe'
    if (-not (Test-Path -LiteralPath $engine)) { throw 'Native executable missing after build' }
    $dumpbinCommand = Get-Command dumpbin -ErrorAction SilentlyContinue
    $dumpbinPath = if ($dumpbinCommand) { $dumpbinCommand.Source } else {
        (Get-ChildItem -LiteralPath (Join-Path $vs 'VC/Tools/MSVC') -Recurse -File -Filter dumpbin.exe -ErrorAction SilentlyContinue |
            Where-Object FullName -Match 'Hostx64\\x64' | Select-Object -First 1 -ExpandProperty FullName)
    }
    if (-not $dumpbinPath) { throw 'dumpbin.exe is required to verify static CRT linkage.' }
        $dependencies = & $dumpbinPath /DEPENDENTS $engine | Out-String
        if ($LASTEXITCODE -ne 0) { throw 'Native dependency inspection failed.' }
        if ($dependencies -match '(?i)VCRUNTIME140|MSVCP140|ucrtbase') {
            throw 'Native engine still has a dynamic MSVC CRT dependency.'
        }
    Write-Output $engine
} finally { Pop-Location; $env:CARGO_ENCODED_RUSTFLAGS = $oldRustFlags }

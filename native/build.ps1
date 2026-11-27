<#
.SYNOPSIS
  Builds osmium_native-<arch>.dll into artifacts/native/win-<arch>/ with build-info.txt beside it.

.DESCRIPTION
  Finds the newest Visual Studio with the required parts (vswhere), sets up its environment (vcvarsall with the
  Spectre-mitigated libraries), fetches and checks the upstream sources, builds with CMake and Ninja, checks the
  binary (check_binary.ps1) and writes build-info.txt. The PDB and the linker map go to
  artifacts/native-symbols/win-<arch>/ (CI artifacts, never shipped). Uses at most OSN_JOBS parallel compiles
  (default 2).

.PARAMETER Arch      x64 or arm64 (arm64 is cross-compiled on an x64 host).
.PARAMETER Compiler  msvc (shipped) or clang-cl (a differential build into artifacts/native-clangcl/, never shipped).
.PARAMETER From      Take the upstream archives from this folder instead of downloading them.
.PARAMETER Tests     Also build the self-test and the marker library into artifacts/native-test/win-<arch>/.
.PARAMETER Analyze   Only compile the interface layer with /analyze; findings fail. Produces no library.
.PARAMETER Fuzz      x64 only: build the fuzz harnesses with /fsanitize=address and /fsanitize=fuzzer into
                     artifacts/native-fuzz/win-x64/, write the seed inputs there and run each harness for FuzzSeconds
                     (0: build only). Produces no library.
.PARAMETER NoSpectre Measurements only: build without /Qspectre into artifacts/native-nospectre/win-<arch>/, to
                     measure its cost (native-bench.yml). Never shipped.
.PARAMETER Version   Osmium version for the version resource.
#>
param(
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [ValidateSet('msvc', 'clang-cl')][string]$Compiler = 'msvc',
    [string]$From = '',
    [switch]$Tests,
    [switch]$Analyze,
    [switch]$Fuzz,
    [int]$FuzzSeconds = 0,
    [switch]$NoSpectre,
    [string]$Version = '0.0.0'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$rid = "win-$Arch"
$jobs = if ($env:OSN_JOBS) { $env:OSN_JOBS } else { '2' }

function Invoke-Checked([string]$what, [scriptblock]$block) {
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

# ---------------------------------------------------------------------------------------------------- Visual Studio
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found: Visual Studio 2022 or later is required' }
$components = @(
    'Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
    'Microsoft.VisualStudio.Component.VC.Tools.ARM64',
    'Microsoft.VisualStudio.Component.VC.Runtimes.x86.x64.Spectre',
    'Microsoft.VisualStudio.Component.VC.Runtimes.ARM64.Spectre')
if ($Compiler -eq 'clang-cl') { $components += 'Microsoft.VisualStudio.Component.VC.Llvm.Clang' }
foreach ($c in $components) {
    $found = & $vswhere -latest -products * -requires $c -property installationPath
    if (-not $found) { throw "Visual Studio is missing the component $c" }
}
$vs = & $vswhere -latest -products * -requires $components -property installationPath
$vsVersion = & $vswhere -latest -products * -requires $components -property catalog_productDisplayVersion
Write-Host "Visual Studio $vsVersion at $vs"

$hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$vcTarget = if ($hostArch -eq $Arch) { $Arch } else { "${hostArch}_$Arch" }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat'
$envLines = cmd /c "`"$vcvars`" $vcTarget -vcvars_spectre_libs=spectre > nul && set"
if ($LASTEXITCODE -ne 0) { throw "vcvarsall $vcTarget failed" }
foreach ($line in $envLines) {
    if ($line -match '^([^=]+)=(.*)$') { Set-Item -Path "env:$($Matches[1])" -Value $Matches[2] }
}
$toolset = $env:VCToolsVersion
Write-Host "MSVC toolset $toolset ($vcTarget)"

# ---------------------------------------------------------------------------------------------------- sources
$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = (Get-Command python3).Source }
if ($From) { Invoke-Checked 'fetch_sources' { & $python -I (Join-Path $root 'native/fetch_sources.py') --from $From } }
else { Invoke-Checked 'fetch_sources' { & $python -I (Join-Path $root 'native/fetch_sources.py') } }
Invoke-Checked 'check_sources' { & $python -I (Join-Path $root 'native/check_sources.py') }

# ---------------------------------------------------------------------------------------------------- configure, build
if ($Fuzz -and ($Arch -ne 'x64' -or $Compiler -ne 'msvc')) { throw '-Fuzz is built for x64 with MSVC only' }
$suffix = if ($Compiler -eq 'clang-cl') { '-clangcl' } elseif ($Analyze) { '-analyze' } elseif ($Fuzz) { '-fuzz' } elseif ($NoSpectre) { '-nospectre' } else { '' }
$build = Join-Path $root "artifacts/native-build/$rid$suffix"
if (Test-Path $build) { Remove-Item -Recurse -Force $build }
$cmakeArgs = @('-S', (Join-Path $root 'native'), '-B', $build, '-G', 'Ninja', '-DCMAKE_BUILD_TYPE=Release',
    "-DOSN_ARCH=$Arch", "-DOSN_VERSION_DEFINE=$Version", "-DOSN_BUILD_TESTS=$(if ($Tests) { 'ON' } else { 'OFF' })")
if ($Compiler -eq 'clang-cl') {
    $cmakeArgs += @('-DCMAKE_C_COMPILER=clang-cl')
    if ($Arch -eq 'arm64') { $cmakeArgs += @('-DCMAKE_C_COMPILER_TARGET=aarch64-pc-windows-msvc') }
} else {
    $cmakeArgs += @('-DCMAKE_C_COMPILER=cl')
}
if ($Analyze) { $cmakeArgs += @('-DOSN_ANALYZE=ON') }
if ($Fuzz) { $cmakeArgs += @('-DOSN_FUZZ=ON') }
if ($NoSpectre) { $cmakeArgs += @('-DOSN_BENCH_NO_SPECTRE=ON') }
Invoke-Checked 'cmake configure' { cmake @cmakeArgs }

if ($Fuzz) {
    $harnesses = 'fuzz_lzma_encode', 'fuzz_zstd_encode', 'fuzz_shim_args'
    Invoke-Checked 'fuzz build' { ninja -C $build -j $jobs @harnesses }
    $fuzzOut = Join-Path $root 'artifacts/native-fuzz/win-x64'
    if (Test-Path $fuzzOut) { Remove-Item -Recurse -Force $fuzzOut }
    New-Item -ItemType Directory -Force (Join-Path $fuzzOut 'findings') | Out-Null
    Invoke-Checked 'seeds' { & $python -I (Join-Path $root 'native/fuzz/make_seeds.py') (Join-Path $fuzzOut 'seeds') }
    foreach ($h in $harnesses) {
        Copy-Item (Join-Path $build "$h.exe") $fuzzOut
        if ($FuzzSeconds -le 0) { continue }
        $corpus = Join-Path $fuzzOut "corpus/$h"
        New-Item -ItemType Directory -Force $corpus | Out-Null
        Write-Host "== $h for $FuzzSeconds s (AddressSanitizer)"
        Invoke-Checked $h { & (Join-Path $fuzzOut "$h.exe") "-max_total_time=$FuzzSeconds" -timeout=60 -rss_limit_mb=2560 `
            "-artifact_prefix=$(Join-Path $fuzzOut 'findings')\$h-" $corpus (Join-Path $fuzzOut "seeds/$h") }
    }
    exit 0
}

if ($Analyze) {
    Invoke-Checked 'analyze' { ninja -C $build -j $jobs osn_core }
    Write-Host 'The interface layer compiled with /analyze without findings.'
    exit 0
}

$log = Join-Path $build 'build.log'
ninja -C $build -j $jobs 2>&1 | Tee-Object -FilePath $log
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$warnings = (Select-String -Path $log -Pattern 'warning [A-Z]+[0-9]+' -AllMatches | Measure-Object).Count
Write-Host "compiler and linker warnings: $warnings"

$dll = Join-Path $build "osmium_native-$Arch.dll"
$pdb = Join-Path $build "osmium_native-$Arch.pdb"
$map = Join-Path $build "osmium_native-$Arch.map"

# ---------------------------------------------------------------------------------------------------- checks
Invoke-Checked 'check_binary' { & (Join-Path $root 'native/check_binary.ps1') -Dll $dll -Arch $Arch -Map $map -Compiler $Compiler }

# ---------------------------------------------------------------------------------------------------- output
$outBase = if ($Compiler -eq 'clang-cl') { 'artifacts/native-clangcl' } elseif ($NoSpectre) { 'artifacts/native-nospectre' } else { 'artifacts/native' }
$out = Join-Path $root "$outBase/$rid"
$symbols = Join-Path $root "artifacts/native-symbols/$rid$suffix"
foreach ($d in @($out, $symbols)) {
    if (Test-Path $d) { Remove-Item -Recurse -Force $d }
    New-Item -ItemType Directory -Force $d | Out-Null
}
Copy-Item $dll $out
Copy-Item $pdb, $map $symbols

function Gen([string]$key) {
    (Get-Content (Join-Path $root 'native/generation.txt') | Where-Object { $_ -match "^$key=" }) -replace "^$key=", ''
}
$first = { param($exe) try { (& $exe 2>&1 | Where-Object { "$_".Trim() } | Select-Object -First 1) } catch { 'none' } }
$sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
if (-not $sevenZip) { $sevenZip = Join-Path $env:ProgramFiles '7-Zip\7z.exe' }
$clVersion = if ($Compiler -eq 'clang-cl') { (& clang-cl --version 2>&1 | Select-Object -First 1) } else { (cmd /c 'cl 2>&1' | Select-Object -First 1) }
$image = "$env:ImageOS $env:ImageVersion".Trim()
if (-not $image) { $image = 'local' }
$info = @(
    "target=$rid",
    "file=osmium_native-$Arch.dll",
    "sha256=$((Get-FileHash -Algorithm SHA256 (Join-Path $out "osmium_native-$Arch.dll")).Hash.ToLowerInvariant())",
    "size=$((Get-Item (Join-Path $out "osmium_native-$Arch.dll")).Length)",
    "generation=$(Gen 'generation')",
    "abi=$(Gen 'abi')",
    "versions=lzma-sdk $(Gen 'lzma-sdk'); zstd $(Gen 'zstd')",
    "sources_lock_sha256=$((Get-FileHash -Algorithm SHA256 (Join-Path $root 'native/sources.lock')).Hash.ToLowerInvariant())",
    "sources_files_sha256=$((Get-FileHash -Algorithm SHA256 (Join-Path $root 'native/sources.files')).Hash.ToLowerInvariant())",
    "visual_studio=$vsVersion",
    "msvc_toolset=$toolset",
    "compiler_version=$clVersion",
    "cmake=$(& $first cmake --version)",
    "ninja=$(ninja --version)",
    "python=$(& $python --version 2>&1)",
    "7zip=$(if (Test-Path $sevenZip) { & $first $sevenZip } else { 'none' })",
    "os=$([Environment]::OSVersion.VersionString) $env:PROCESSOR_ARCHITECTURE",
    "image=$image",
    "warnings=$warnings"
) + (Get-Content (Join-Path $build 'osn_flags.txt'))
$info | Set-Content -Encoding utf8NoBOM (Join-Path $out 'build-info.txt')
$info | ForEach-Object { Write-Host $_ }

if ($Tests) {
    $testOut = Join-Path $root "artifacts/native-test/$rid$suffix"
    if (Test-Path $testOut) { Remove-Item -Recurse -Force $testOut }
    New-Item -ItemType Directory -Force $testOut | Out-Null
    Copy-Item (Join-Path $build "osmium_native_marker-$Arch.dll"), (Join-Path $build 'osn_selftest.exe'),
        (Join-Path $build 'osn_bench.exe') $testOut
    if ($Arch -eq $hostArch) {
        Invoke-Checked 'self-test' { & (Join-Path $build 'osn_selftest.exe') }
    } else {
        Write-Host "The self-test for $Arch runs on an $Arch machine (artifacts/native-test/$rid)."
    }
}

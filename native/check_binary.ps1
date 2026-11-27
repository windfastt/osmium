<#
.SYNOPSIS
  Checks a Windows build of osmium_native with dumpbin; any failure throws.

.DESCRIPTION
  - dependencies: only KERNEL32.dll
  - headers: machine type matching the file name; Dynamic base, High Entropy VA, NX compatible, Control Flow Guard;
    CET compatible on x64; the dependent-load flag 0x800 (LOAD_LIBRARY_SEARCH_SYSTEM32) in the load configuration
  - exports: exactly the osn_* functions of osmium_native.def
  - no decoder: no ZSTD_decompress*, ZSTD_createDCtx*, ZSTD_DCtx*, ZSTD_decodeFrame* or LzmaDec_* symbol in the
    linker map; the FSE/HUF table readers kept by /OPT:REF are listed for review
  - ARM64 (MSVC): return-address signing (/guard:signret): pacibsp at the start of the two encode functions, which
    save the link register (leaf functions such as osn_abi need none); the count for the whole image is printed
#>
param(
    [Parameter(Mandatory)][string]$Dll,
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Arch,
    [string]$Map = '',
    [ValidateSet('msvc', 'clang-cl')][string]$Compiler = 'msvc'
)
$ErrorActionPreference = 'Stop'
$script:failures = 0
function Bad([string]$m) { Write-Host "check_binary: FAIL: $m"; $script:failures++ }
function Ok([string]$m) { Write-Host "check_binary: ok: $m" }

if (-not (Test-Path $Dll)) { throw "no such file: $Dll" }
$dumpbin = (Get-Command dumpbin -ErrorAction SilentlyContinue).Source
if (-not $dumpbin) { throw 'dumpbin not found (run from a Visual Studio developer environment)' }

# Dependencies
$deps = & $dumpbin /nologo /dependents $Dll
$names = @($deps | Where-Object { $_ -match '^\s+\S+\.dll\s*$' } | ForEach-Object { $_.Trim() })
if ($names.Count -eq 1 -and $names[0] -ieq 'KERNEL32.dll') { Ok "dependencies: $($names -join ', ')" }
else { Bad "dependencies are '$($names -join ', ')', expected only KERNEL32.dll" }

# Headers
$headers = (& $dumpbin /nologo /headers $Dll) -join "`n"
$machine = if ($Arch -eq 'x64') { '8664 machine \(x64\)' } else { 'AA64 machine \(ARM64\)' }
if ($headers -match $machine) { Ok "machine $Arch" } else { Bad "machine type is not $Arch" }
foreach ($flag in @('Dynamic base', 'High Entropy Virtual Addresses', 'NX compatible', 'Control Flow Guard')) {
    if ($headers -match [regex]::Escape($flag)) { Ok $flag } else { Bad "missing: $flag" }
}
if ($Arch -eq 'x64') {
    if ($headers -match 'CET compatible') { Ok 'CET compatible' } else { Bad 'missing: CET compatible' }
}
$loadConfig = (& $dumpbin /nologo /loadconfig $Dll) -join "`n"
if ($loadConfig -match '([0-9A-Fa-f]+)\s+Dependent Load Flag') {
    $value = [Convert]::ToInt32($Matches[1], 16)
    if ($value -eq 0x800) { Ok 'dependent load flag 0x800 (LOAD_LIBRARY_SEARCH_SYSTEM32)' }
    else { Bad ('dependent load flag is 0x{0:X}, expected 0x800' -f $value) }
} else { Bad 'no dependent load flag in the load configuration' }
if ($loadConfig -match 'CF Instrumented') { Ok 'CFG instrumented' } else { Bad 'load configuration lacks CF Instrumented' }

# Exports
$def = Get-Content (Join-Path $PSScriptRoot 'osmium_native.def') | Where-Object { $_ -match '^\s+osn_\w+\s*$' } |
    ForEach-Object { $_.Trim() } | Sort-Object
$exports = & $dumpbin /nologo /exports $Dll | Where-Object { $_ -match '^\s+\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+(\S+)' } |
    ForEach-Object { if ($_ -match '^\s+\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+(\S+)') { $Matches[1] } } | Sort-Object
if ((@($exports) -join ' ') -eq (@($def) -join ' ')) { Ok "exports: $($exports -join ' ')" }
else { Bad "exports are '$($exports -join ' ')', expected '$($def -join ' ')'" }

# No decoder (linker map)
$decoder = '\b(ZSTD_decompress|ZSTD_createDCtx|ZSTD_DCtx|ZSTD_decodeFrame|ZSTD_decodeLiteralsBlock|ZSTD_decodeSeqHeaders|LzmaDec_|LzmaDecode)'
if ($Map -and (Test-Path $Map)) {
    $mapText = Get-Content $Map
    $hits = @($mapText | Select-String -Pattern $decoder)
    if ($hits.Count -eq 0) { Ok 'no decoder symbol in the linker map' }
    else { Bad "decoder symbols in the linker map: $($hits | Select-Object -First 5)" }
    $readers = $mapText | Select-String -Pattern '\b(FSE_\w*[Dd]ecompress\w*|FSE_readNCount\w*|FSE_buildDTable\w*|HUF_readStats\w*|HUF_readCTable\w*|HUF_readDTable\w*)' -AllMatches |
        ForEach-Object { $_.Matches.Value } | Sort-Object -Unique
    Write-Host "check_binary: table readers kept by the linker (for review): $($readers -join ' ')"
} else { Bad 'no linker map given: the decoder check needs it' }

# ARM64 return-address signing
if ($Arch -eq 'arm64') {
    $pdbDir = Split-Path $Dll
    $disasm = & $dumpbin /nologo /disasm:nobytes $Dll
    $total = @($disasm | Select-String -Pattern '\bpacibsp\b').Count
    Write-Host "check_binary: pacibsp instructions in the image: $total"
    $results = @{}
    for ($i = 0; $i -lt $disasm.Count; $i++) {
        if ($disasm[$i] -match '^(osn_\w+):\s*$') {
            $name = $Matches[1]
            $window = $disasm[($i + 1)..([Math]::Min($i + 4, $disasm.Count - 1))] -join "`n"
            $results[$name] = $window -match '\bpacibsp\b'
        }
    }
    if ($results.Count -eq 0) { Bad 'no osn_* labels in the disassembly (PDB not found beside the DLL?)' }
    foreach ($k in ($results.Keys | Sort-Object)) { Write-Host "check_binary: $k starts with pacibsp: $($results[$k])" }
    if ($Compiler -eq 'msvc') {
        foreach ($required in @('osn_lzma_encode', 'osn_zstd_encode')) {
            if ($results[$required]) { Ok "$required signs its return address" } else { Bad "$required does not start with pacibsp" }
        }
    }
}

if ($script:failures -gt 0) { throw "$($script:failures) binary check(s) failed" }
Write-Host 'check_binary: all checks passed'

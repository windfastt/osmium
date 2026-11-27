<#
.SYNOPSIS
  Times Osmium's two encoder generations against 7-Zip on one corpus folder (native-bench.yml; indicative only:
  shared runners are noisy).

.DESCRIPTION
  Each group is a set of sides. Every round runs every side once, compress and then extract, in an order that rotates
  every round, so a change in the runner's load spreads over all sides. Per side it reports the archive size, the
  median, minimum and maximum of compress wall and CPU time, CPU / wall, the median extract wall and the peak working
  set. CPU time is the process's TotalProcessorTime (GetProcessTimes) and peak memory its PeakWorkingSetSize, both read
  from the process handle after it exited. Threads are limited with the processor affinity, which the child process
  inherits. After the last round every archive's extracted files are checked against the corpus manifest.

.PARAMETER Osmium   osmium.exe of a publish folder (the native library beside it).
.PARAMETER Corpus   The corpus folder; <Corpus>.MANIFEST.sha256 lies beside it (tools/bench/make_corpora.py).
.PARAMETER Out      Tab-separated results, appended.
#>
param(
    [Parameter(Mandatory)][string]$Osmium,
    [Parameter(Mandatory)][string]$Corpus,
    [Parameter(Mandatory)][string]$Out,
    [string]$SevenZip = (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
    [int]$Rounds = 3,
    [int[]]$Threads = @(1, 2, 4),
    [string[]]$Groups = @('codec', 'profiles')
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OsnMemory {
    [StructLayout(LayoutKind.Sequential)]
    struct Counters {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }
    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    static extern bool GetProcessMemoryInfo(IntPtr process, out Counters counters, uint size);
    public static double PeakMiB(IntPtr process) {
        Counters c;
        if (!GetProcessMemoryInfo(process, out c, (uint)Marshal.SizeOf(typeof(Counters)))) return -1;
        return c.PeakWorkingSetSize.ToUInt64() / 1048576.0;
    }
}
'@

$Osmium = (Resolve-Path $Osmium).Path
$corpusFull = (Resolve-Path $Corpus).Path
$parent = Split-Path $corpusFull -Parent
$name = Split-Path $corpusFull -Leaf
$manifest = Join-Path $parent "$name.MANIFEST.sha256"
if (-not (Test-Path $manifest)) { throw "no manifest $manifest" }
$work = Join-Path $parent 's8-work'
New-Item -ItemType Directory -Force $work | Out-Null
$self = [System.Diagnostics.Process]::GetCurrentProcess()
$allCpus = $self.ProcessorAffinity

function Invoke-Timed([string]$exe, [string[]]$arguments, [string]$gen, [int]$cpus) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($exe)
    foreach ($a in $arguments) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $parent
    [void]$psi.Environment.Remove('OSMIUM_ENCODERS')
    if ($gen) { $psi.Environment['OSMIUM_ENCODERS'] = $gen }
    $self.ProcessorAffinity = [IntPtr](([long]1 -shl $cpus) - 1)   # inherited by the child
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $p = [System.Diagnostics.Process]::Start($psi)
        $stdout = $p.StandardOutput.ReadToEndAsync()
        $stderr = $p.StandardError.ReadToEndAsync()
        $p.WaitForExit()
        $wall = $sw.Elapsed.TotalSeconds
    } finally {
        $self.ProcessorAffinity = $allCpus
    }
    [void]$stdout.Result
    if ($p.ExitCode -ne 0) { throw "$exe $($arguments -join ' ') failed ($($p.ExitCode)): $($stderr.Result)" }
    $r = [pscustomobject]@{ Wall = $wall; Cpu = $p.TotalProcessorTime.TotalSeconds; PeakMiB = [OsnMemory]::PeakMiB($p.Handle) }
    $p.Dispose()
    return $r
}

$script:n = 0
function New-Side([string]$kind, [string]$label, [string[]]$options, [string]$gen, [int]$t, [bool]$test) {
    $script:n++
    $ext = if ($kind -eq 'osmium') { 'osa' } else { '7z' }
    [pscustomobject]@{
        Kind = $kind; Label = $label; Options = $options; Gen = $gen; Threads = $t; Test = $test
        Archive = Join-Path $work "a$($script:n).$ext"; Dest = Join-Path $work "x$($script:n)"
        EncWall = [System.Collections.Generic.List[double]]::new(); EncCpu = [System.Collections.Generic.List[double]]::new()
        DecWall = [System.Collections.Generic.List[double]]::new(); Peak = 0.0; Size = 0L
        Sizes = [System.Collections.Generic.HashSet[long]]::new()
    }
}
function Both([string]$label, [string[]]$options, [int]$t) {
    (New-Side 'osmium' "$label managed-1" $options 'managed' $t $false), (New-Side 'osmium' "$label native-1" $options 'native' $t $false)
}
function Seven([string]$label, [string[]]$options, [int]$t, [bool]$test = $false) { New-Side '7z' $label $options '' $t $test }

function Invoke-Encode($s) {
    Remove-Item -Force $s.Archive -ErrorAction SilentlyContinue
    $t = $s.Threads
    if ($s.Kind -eq 'osmium') {
        return Invoke-Timed $Osmium (@('a', $s.Archive, $name) + $s.Options + @('--threads', "$t", '--no-plan-log')) $s.Gen $t
    }
    $r = Invoke-Timed $SevenZip (@('a', $s.Archive, $name) + $s.Options + @("-mmt=$t")) '' $t
    if ($s.Test) {
        $q = Invoke-Timed $SevenZip @('t', $s.Archive, "-mmt=$t") '' $t
        $r = [pscustomobject]@{ Wall = $r.Wall + $q.Wall; Cpu = $r.Cpu + $q.Cpu; PeakMiB = [Math]::Max($r.PeakMiB, $q.PeakMiB) }
    }
    return $r
}
function Invoke-Decode($s) {
    Remove-Item -Recurse -Force $s.Dest -ErrorAction SilentlyContinue
    if ($s.Kind -eq 'osmium') {
        New-Item -ItemType Directory $s.Dest | Out-Null
        return Invoke-Timed $Osmium @('x', $s.Archive, $s.Dest) $s.Gen $s.Threads
    }
    return Invoke-Timed $SevenZip @('x', '-y', "-o$($s.Dest)", $s.Archive, "-mmt=$($s.Threads)") '' $s.Threads
}

function Test-Extracted([string]$dest) {
    $lines = @(Get-Content $manifest | Where-Object { $_ })
    $files = @(Get-ChildItem -Recurse -File $dest).Count
    if ($files -ne $lines.Count) { return "MISMATCH ($files files, manifest $($lines.Count))" }
    foreach ($l in $lines) {
        $sha, $path = $l -split '  ', 2
        $h = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $dest $path)).Hash.ToLowerInvariant()
        if ($h -ne $sha) { return "MISMATCH $path" }
    }
    return 'ok'
}

function Median($values) {
    $s = @($values | Sort-Object)
    $k = $s.Count
    if ($k % 2) { return $s[($k - 1) / 2] }
    return ($s[$k / 2 - 1] + $s[$k / 2]) / 2
}

function Get-Load { (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average }

function Measure-Group([string]$title, $sides) {
    $sides = @($sides)
    $k = $sides.Count
    $load = Get-Load
    Write-Host "== $title ($Rounds rounds; CPU load $load % at the start)"
    for ($r = 0; $r -lt $Rounds; $r++) {
        $order = @(for ($j = 0; $j -lt $k; $j++) { $sides[($j + $r) % $k] })
        foreach ($s in $order) {
            $m = Invoke-Encode $s
            $s.EncWall.Add($m.Wall); $s.EncCpu.Add($m.Cpu); $s.Peak = [Math]::Max($s.Peak, $m.PeakMiB)
            $s.Size = (Get-Item $s.Archive).Length; [void]$s.Sizes.Add($s.Size)
        }
        foreach ($s in $order) { $m = Invoke-Decode $s; $s.DecWall.Add($m.Wall) }
        Write-Host "   round $($r + 1) done, CPU load $(Get-Load) %"
    }
    "# $title; $Rounds interleaved rounds; CPU load $load % at the start, $(Get-Load) % at the end" | Add-Content $Out
    "label`tsize`tenc_wall_med`tenc_wall_min`tenc_wall_max`tcpu_med`tcpu_min`tcpu_max`tcpu/wall`tdec_wall_med`tpeak_ws_MiB`tsizes_equal`tcheck" | Add-Content $Out
    foreach ($s in $sides) {
        $w = Median $s.EncWall; $c = Median $s.EncCpu
        $min = { param($v) ($v | Measure-Object -Minimum).Minimum }
        $max = { param($v) ($v | Measure-Object -Maximum).Maximum }
        ("{0}`t{1}`t{2:F2}`t{3:F2}`t{4:F2}`t{5:F2}`t{6:F2}`t{7:F2}`t{8:F2}`t{9:F2}`t{10:F0}`t{11}`t{12}" -f $s.Label, $s.Size, $w,
            (& $min $s.EncWall), (& $max $s.EncWall), $c, (& $min $s.EncCpu), (& $max $s.EncCpu), ($c / $w),
            (Median $s.DecWall), $s.Peak, ($s.Sizes.Count -eq 1), (Test-Extracted $s.Dest)) | Add-Content $Out
        Remove-Item -Recurse -Force $s.Archive, $s.Dest -ErrorAction SilentlyContinue
    }
}

$L32 = 'lzma(dict=32M)'
$sz = '-m0=LZMA:d=32m:fb=273:mf=bt4:lc=3:lp=0:pb=2'
foreach ($g in $Groups) {
    if ($g -eq 'codec') {
        foreach ($t in @(1, 2)) {
            if ($Threads -notcontains $t) { continue }
            Measure-Group "lzma(dict=32M), $t thread(s)" (@(Both "lzma(dict=32M) T$t" @('--pipeline', $L32, '--no-verify') $t) +
                @(Seven "7z LZMA d32m fb273 bt4 mmt$t" @($sz, '-ms=on') $t))
        }
        if ($Threads -contains 4) {
            Measure-Group 'lzma(dict=32M) in 4 MiB blocks, 4 threads' (@(Both 'lzma(dict=32M) T4 block 4M' @('--pipeline', $L32, '--block', '4M', '--no-verify') 4) +
                @(Seven '7z LZMA2 d32m fb273 bt4 mmt4' @('-m0=LZMA2:d=32m:fb=273:mf=bt4:lc=3:lp=0:pb=2', '-ms=on') 4))
        }
        Measure-Group 'zstd levels 3 and 19, 1 thread (no zstd tool on the runner: generations only)' (
            @(Both 'zstd(level=3) T1' @('--pipeline', 'zstd(level=3)', '--no-verify') 1) +
            @(Both 'zstd(level=19) T1' @('--pipeline', 'zstd(level=19)', '--no-verify') 1))
    }
    if ($g -eq 'profiles') {
        foreach ($t in $Threads) {
            $sides = @()
            foreach ($p in 'fast', 'balanced', 'max') { $sides += Both "$p T$t no-verify" @('--profile', $p, '--no-verify') $t }
            $sides += Both "balanced T$t verify (default)" @('--profile', 'balanced') $t
            $sides += Seven "7z -mx5 mmt$t" @('-mx=5') $t
            $sides += Seven "7z -mx9 mmt$t" @('-mx=9') $t
            $sides += Seven "7z -mx9 mmt$t + 7z t" @('-mx=9') $t $true
            Measure-Group "profiles, $t thread(s)" $sides
        }
    }
}

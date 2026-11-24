<#
.SYNOPSIS
  Builds Osmium for Windows: co-publishes the tray app and the CLI (self-contained, one shared runtime),
  a standalone single-file CLI, a portable ZIP and the Inno Setup installer.
.EXAMPLE
  pwsh build/publish.ps1                       # x64, version from Directory.Build.props
  pwsh build/publish.ps1 -Arch arm64 -Version 1.2.3
#>
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
    [string] $Version = '',
    [switch] $SkipInstaller
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

if (-not $Version) {
    $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Select-Object -First 1
}
$numeric = (($Version -split '-')[0] -split '\+')[0]
while (($numeric -split '\.').Count -lt 4) { $numeric += '.0' }
$rid = "win-$Arch"
$out = "artifacts/publish/$rid"
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue

Write-Host "== Publishing Osmium $Version ($rid)"
foreach ($p in 'src/Osmium.App', 'src/Osmium.Cli') {
    dotnet publish $p -c Release -r $rid --self-contained -p:PublishReadyToRun=true -p:Version=$Version -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $p" }
}
dotnet publish src/Osmium.Cli -c Release -r $rid --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o "artifacts/cli-$rid"
if ($LASTEXITCODE -ne 0) { throw 'single-file CLI publish failed' }
Move-Item -Force "artifacts/cli-$rid/osmium.exe" "artifacts/osmium-$Version-$rid.exe"

Write-Host '== Portable ZIP'
$portable = "artifacts/portable-$rid"
Remove-Item -Recurse -Force $portable -ErrorAction SilentlyContinue
Copy-Item -Recurse $out $portable
New-Item -ItemType Directory -Force "$portable/icons" | Out-Null
Copy-Item assets/archive.ico, assets/osmium.ico "$portable/icons/"
Set-Content "$portable/osmium.portable" 'Data is stored in .\data next to the executable.'
Compress-Archive -Force -Path "$portable/*" -DestinationPath "artifacts/Osmium-$Version-$rid-portable.zip"

if (-not $SkipInstaller) {
    Write-Host '== Installer'
    $iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    if (-not (Test-Path $iscc)) {
        choco install innosetup -y --no-progress | Out-Host
    }
    & $iscc /Qp "/DAppVersion=$Version" "/DNumericVersion=$numeric" "/DArch=$Arch" installer/osmium.iss
    if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
}
Get-ChildItem artifacts -File | Format-Table Name, Length

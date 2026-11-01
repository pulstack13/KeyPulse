$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$PayloadDir = Join-Path $PSScriptRoot 'payload'
$OutputDir = Join-Path $RepoRoot 'release'
$AppDist = Join-Path $RepoRoot 'dist'
$Compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $Compiler)) {
    $Compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $Compiler)) { throw 'Could not find the .NET Framework C# compiler (csc.exe).' }
if (-not (Test-Path -LiteralPath (Join-Path $AppDist 'KeyPulse.exe'))) {
    throw 'Build the app first by running .\build.ps1 from the repository root.'
}

New-Item -ItemType Directory -Force -Path $PayloadDir, $OutputDir | Out-Null
foreach ($FileName in @(
    'KeyPulse.exe',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.WinForms.dll',
    'WebView2Loader.dll'
)) {
    $Source = Join-Path $AppDist $FileName
    if (-not (Test-Path -LiteralPath $Source)) { throw "Required app file is missing: $Source" }
    Copy-Item -LiteralPath $Source -Destination (Join-Path $PayloadDir $FileName) -Force
}
Copy-Item -LiteralPath (Join-Path $RepoRoot 'ui\keypulse-logo.png') -Destination (Join-Path $PayloadDir 'keypulse-logo.png') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'ui\keypulse-logo.ico') -Destination (Join-Path $PayloadDir 'keypulse-logo.ico') -Force

$Exe = Join-Path $OutputDir 'KeyPulse-Setup.exe'
$Arguments = @(
    '/nologo', '/target:winexe', '/optimize+',
    "/out:$Exe",
    "/win32icon:$(Join-Path $PayloadDir 'keypulse-logo.ico')",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    "/resource:$(Join-Path $PayloadDir 'KeyPulse.exe'),KeyPulseSetup.Payload.KeyPulse.exe",
    "/resource:$(Join-Path $PayloadDir 'Microsoft.Web.WebView2.Core.dll'),KeyPulseSetup.Payload.Microsoft.Web.WebView2.Core.dll",
    "/resource:$(Join-Path $PayloadDir 'Microsoft.Web.WebView2.WinForms.dll'),KeyPulseSetup.Payload.Microsoft.Web.WebView2.WinForms.dll",
    "/resource:$(Join-Path $PayloadDir 'WebView2Loader.dll'),KeyPulseSetup.Payload.WebView2Loader.dll",
    "/resource:$(Join-Path $PayloadDir 'keypulse-logo.png'),KeyPulseSetup.Payload.keypulse-logo.png",
    "/resource:$(Join-Path $PayloadDir 'keypulse-logo.ico'),KeyPulseSetup.Payload.keypulse-logo.ico",
    (Join-Path $PSScriptRoot 'SetupProgram.cs')
)
& $Compiler @Arguments
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed (exit $LASTEXITCODE)." }
Write-Host "Built $Exe"

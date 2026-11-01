$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) {
    throw "Could not find the .NET Framework C# compiler (csc.exe)."
}

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$sources = @(Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) {
    throw "No source files found."
}

$out = Join-Path $dist "KeyPulse.exe"
$lib = Join-Path $root "lib"
$ui = Join-Path $root "ui\index.html"
$logo = Join-Path $root "ui\keypulse-logo.png"
$icon = Join-Path $root "ui\keypulse-logo.ico"
$manifest = Join-Path $root "app.manifest"
$embeddedUi = Join-Path $dist "KeyPulse-embedded-ui.html"

foreach ($required in @(
    (Join-Path $lib "Microsoft.Web.WebView2.Core.dll"),
    (Join-Path $lib "Microsoft.Web.WebView2.WinForms.dll"),
    (Join-Path $lib "WebView2Loader.dll"),
    $ui,
    $logo,
    $icon,
    $manifest
)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required build file is missing: $required"
    }
}

$logoData = [Convert]::ToBase64String([IO.File]::ReadAllBytes($logo))
$html = [IO.File]::ReadAllText($ui)
$html = $html.Replace("__KEYPULSE_LOGO_DATA__", "data:image/png;base64," + $logoData)
[IO.File]::WriteAllText($embeddedUi, $html, (New-Object System.Text.UTF8Encoding($false)))

& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/win32icon:$icon" "/win32manifest:$manifest" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    "/r:$(Join-Path $lib 'Microsoft.Web.WebView2.Core.dll')" "/r:$(Join-Path $lib 'Microsoft.Web.WebView2.WinForms.dll')" `
    "/resource:$embeddedUi,KeyTyper.ui.index.html" "/resource:$logo,KeyPulse.logo.png" `
    "/out:$out" $sources

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}
Remove-Item -LiteralPath $embeddedUi -Force

Copy-Item (Join-Path $lib "Microsoft.Web.WebView2.Core.dll") $dist -Force
Copy-Item (Join-Path $lib "Microsoft.Web.WebView2.WinForms.dll") $dist -Force
Copy-Item (Join-Path $lib "WebView2Loader.dll") $dist -Force

Write-Host "Built: $out"


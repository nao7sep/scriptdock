# Package ScriptDock for Windows into dist/: an Inno Setup .exe installer + a
# portable .zip. Run by CI on windows-latest. Per the app-release-conventions the
# packaging complexity lives here so the release workflow just calls this script.
$ErrorActionPreference = "Stop"
$Repo = Split-Path -Parent $PSScriptRoot
Set-Location $Repo

$AppName = "ScriptDock"
$Project = "src/ScriptDock/ScriptDock.csproj"
$Version = ([regex]::Match((Get-Content Directory.Build.props -Raw), '<Version>(.*?)</Version>')).Groups[1].Value

Remove-Item -Recurse -Force publish-win, dist -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path dist | Out-Null

# Self-contained win-x64 publish -> a folder of ScriptDock.exe + its runtime.
dotnet publish $Project -c Release -r win-x64 --self-contained true -o publish-win

# Debug symbols are useful to developers but add about 105 MB of uncompressed
# payload. Keep them in build outputs, not in the user-facing package.
Get-ChildItem -LiteralPath publish-win -Recurse -File -Filter *.pdb | Remove-Item -Force
if (Get-ChildItem -LiteralPath publish-win -Recurse -File -Filter *.pdb) {
    throw "Debug symbols remain in publish-win after release cleanup."
}
Copy-Item -LiteralPath LICENSE -Destination publish-win/LICENSE.txt
$NuGetPackages = if ($env:NUGET_PACKAGES) {
    $env:NUGET_PACKAGES
} else {
    Join-Path $env:USERPROFILE ".nuget/packages"
}
# Take the notice from the SkiaSharp native package this publish actually resolved, as the macOS
# bundle target does, so a SkiaSharp upgrade cannot leave the appended notice pointing at an old version.
$Deps = Get-Content -LiteralPath "publish-win/$AppName.deps.json" -Raw | ConvertFrom-Json
$SkiaNative = @($Deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like "SkiaSharp.NativeAssets.Win32/*" })
if ($SkiaNative.Count -ne 1) {
    throw "Could not resolve the published SkiaSharp.NativeAssets.Win32 version from $AppName.deps.json."
}
$NativeNotices = Join-Path $NuGetPackages ("skiasharp.nativeassets.win32/" + $SkiaNative[0].Split("/")[1] + "/THIRD-PARTY-NOTICES.txt")
if (-not (Test-Path -LiteralPath $NativeNotices)) {
    throw "SkiaSharp native notices were not restored: $NativeNotices"
}
Copy-Item -LiteralPath THIRD_PARTY_NOTICES -Destination publish-win/THIRD_PARTY_NOTICES.txt
Add-Content -LiteralPath publish-win/THIRD_PARTY_NOTICES.txt -Value (Get-Content -LiteralPath $NativeNotices -Raw)

# Portable: zip the self-contained folder as-is.
Compress-Archive -Path publish-win/* -DestinationPath "dist/$AppName-$Version-win.zip" -Force

# Installer: Inno Setup. iscc is pre-installed on the windows-latest runner;
# local development may use either the per-user or machine-wide installation.
$isccCommand = Get-Command iscc -ErrorAction SilentlyContinue
$iscc = if ($isccCommand) { $isccCommand.Source } else { $null }
if (-not $iscc) {
    $isccCandidates = @(
        if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe" }
        if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe" }
        if ($env:ProgramFiles) { Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe" }
    )
    $iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $iscc) {
    throw "Inno Setup compiler not found. Install Inno Setup 6 for the current user or machine, or add ISCC.exe to PATH."
}
& $iscc "/DMyAppVersion=$Version" scripts/scriptdock.iss

Get-ChildItem dist

# Builds publish\WalkTalk-Setup-<version>.exe: publishes the desktop app into publish\app (WalkTalk.exe with
# every file it needs beside it), then compiles installer\WalkTalk.iss, which packs that folder.
# Requires Inno Setup 6 (winget install JRSoftware.InnoSetup). Bump <Version> in the .csproj files for a new release.
#
#   .\build-installer.ps1                  both steps
#   .\build-installer.ps1 -Step Publish    only publish the app (the CI build signs it before packing)
#   .\build-installer.ps1 -Step Installer  only pack what is already in publish\app
param([ValidateSet("All", "Publish", "Installer")] [string]$Step = "All")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if ($Step -in "All", "Publish") {
    # Start from an empty folder, so a file an earlier version shipped isn't packed again.
    $app = Join-Path $root "publish\app"
    if (Test-Path -LiteralPath $app) { Remove-Item -LiteralPath $app -Recurse -Force }

    dotnet publish "$root\src\WhisperStt.Desktop" -p:PublishProfile=win-x64 -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

if ($Step -in "All", "Installer") {
    $iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

    & $iscc /Q "$root\installer\WalkTalk.iss"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }
    Get-ChildItem "$root\publish\WalkTalk-Setup-*.exe" | Sort-Object LastWriteTime | Select-Object -Last 1 |
        ForEach-Object { "Installer: $($_.FullName) ($([math]::Round($_.Length / 1MB)) MB)" }
}

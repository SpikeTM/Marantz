$ErrorActionPreference = "Stop"

Write-Host "Publishing Marantz Desktop Control..."
Push-Location "$PSScriptRoot\MarantzDesktopControl"
dotnet publish -c Release -r win-x64 --self-contained true
$publishExitCode = $LASTEXITCODE
Pop-Location

if ($publishExitCode -ne 0) {
    throw "Publish failed with exit code $publishExitCode"
}

$candidatePaths = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

$isccPath = $candidatePaths | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $isccPath) {
    throw "Inno Setup compiler was not found in the expected install locations."
}

Write-Host "Building installer package..."
Push-Location "$PSScriptRoot\Installer"
& $isccPath "MarantzDesktopControl.iss"
$isccExitCode = $LASTEXITCODE
Pop-Location

if ($isccExitCode -ne 0) {
    throw "Installer compilation failed with exit code $isccExitCode"
}

$installerPath = "$PSScriptRoot\Installer\MarantzDesktopControl-Setup.exe"
if (-not (Test-Path $installerPath)) {
    throw "Installer output was not found at $installerPath"
}

Write-Host "Done. Installer created at Installer\MarantzDesktopControl-Setup.exe"

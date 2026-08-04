param(
    [string]$SigningCertificate = $env:WINBAT_SIGNING_CERTIFICATE,
    [string]$SigningPassword = $env:WINBAT_SIGNING_PASSWORD,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

# WinBat Lens Release Packaging Script
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ProjectRoot = $PSScriptRoot
Set-Location $ProjectRoot

$Version = "1.1.3"
$DistDir = Join-Path $ProjectRoot "dist"
$PortableDirName = "WinBatLens_v$Version`_Portable_x64"
$PortableDir = Join-Path $DistDir $PortableDirName
$PortableZip = Join-Path $DistDir "$PortableDirName.zip"
$PortableExe = Join-Path $DistDir "$PortableDirName.exe"
$SetupExeName = "WinBatLens_v$Version`_Setup_x64.exe"

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host " Building WinBat Lens v$Version Release " -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan

# 1. Clean old dist directory
if (Test-Path $DistDir) {
    Write-Host "[1/5] Cleaning existing dist directory..." -ForegroundColor Yellow
    Remove-Item $DistDir -Recurse -Force
}
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null

# 2. Dotnet Publish Single-File Self-Contained Release
Write-Host "[2/5] Publishing .NET 10 WPF Single-File Executable..." -ForegroundColor Yellow
# Packaging flags (single-file, self-contained, ReadyToRun, compression) all
# live in WinBatLens.csproj so this script and a plain `dotnet publish` agree.
dotnet publish WinBatLens.csproj -c Release

$PublishExePath = Join-Path $ProjectRoot "bin\Release\net10.0-windows\win-x64\publish\WinBatLens.exe"
if (-not (Test-Path $PublishExePath)) {
    throw "Publish failed: $PublishExePath does not exist!"
}

# 3. Create Portable Single Executable & Portable ZIP
Write-Host "[3/5] Packaging Portable Single Executable & ZIP..." -ForegroundColor Yellow
Copy-Item $PublishExePath -Destination $PortableExe -Force
Write-Host "  -> Portable Single Executable created: $PortableExe" -ForegroundColor Green

New-Item -ItemType Directory -Path $PortableDir -Force | Out-Null
Copy-Item $PublishExePath -Destination (Join-Path $PortableDir "WinBatLens.exe") -Force
if (Test-Path "README.md") { Copy-Item "README.md" -Destination $PortableDir -Force }
if (Test-Path "LICENSE") { Copy-Item "LICENSE" -Destination $PortableDir -Force }

if (Test-Path $PortableZip) { Remove-Item $PortableZip -Force }
Compress-Archive -Path "$PortableDir\*" -DestinationPath $PortableZip -Force
Remove-Item $PortableDir -Recurse -Force
Write-Host "  -> Portable ZIP created: $PortableZip" -ForegroundColor Green

# 4. Create Installer Version (.exe) via Inno Setup
Write-Host "[4/5] Compiling Installer Version (Inno Setup)..." -ForegroundColor Yellow

$PossibleIsccPaths = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)

$IsccPath = $null
foreach ($path in $PossibleIsccPaths) {
    if (Test-Path $path) {
        $IsccPath = $path
        break
    }
}

if (-not $IsccPath) {
    $commandCheck = Get-Command ISCC -ErrorAction SilentlyContinue
    if ($commandCheck) {
        $IsccPath = $commandCheck.Source
    }
}

if ($IsccPath) {
    Write-Host "  Using Inno Setup Compiler: $IsccPath" -ForegroundColor Gray
    & $IsccPath "installer\WinBatLens.iss"
    Write-Host "  -> Installer created: $(Join-Path $DistDir $SetupExeName)" -ForegroundColor Green
} else {
    Write-Warning "Inno Setup Compiler (ISCC.exe) not found. Installer setup generation skipped."
}

# 5. Optional Authenticode signing and verification
$SigningRequested = -not [string]::IsNullOrWhiteSpace($SigningCertificate)
$SignToolPath = $null
$SignToolCommand = Get-Command signtool.exe -ErrorAction SilentlyContinue
if ($SignToolCommand) { $SignToolPath = $SignToolCommand.Source }
if (-not $SignToolPath) {
    $SdkRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { $_ -and (Test-Path $_) }
    foreach ($sdkRoot in $SdkRoots) {
        $candidate = Get-ChildItem -LiteralPath $sdkRoot -Filter signtool.exe -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($candidate) {
            $SignToolPath = $candidate.FullName
            break
        }
    }
}

if ($SigningRequested) {
    if (-not $SignToolPath) { throw "Signing was requested but signtool.exe was not found." }
    if (-not (Test-Path -LiteralPath $SigningCertificate)) { throw "Signing certificate was not found: $SigningCertificate" }

    $signArgs = @("sign", "/fd", "SHA256", "/td", "SHA256", "/tr", $TimestampUrl, "/f", $SigningCertificate)
    if (-not [string]::IsNullOrWhiteSpace($SigningPassword)) { $signArgs += @("/p", $SigningPassword) }

    Get-ChildItem -LiteralPath $DistDir -Filter *.exe -File | ForEach-Object {
        Write-Host "  Signing $($_.Name)..." -ForegroundColor Yellow
        & $SignToolPath @signArgs $_.FullName
        if ($LASTEXITCODE -ne 0) { throw "signtool sign failed for $($_.FullName)" }

        & $SignToolPath verify /pa /all $_.FullName
        if ($LASTEXITCODE -ne 0) { throw "signtool verify failed for $($_.FullName)" }
    }
} else {
    Write-Warning "No signing certificate supplied. Set WINBAT_SIGNING_CERTIFICATE (and optionally WINBAT_SIGNING_PASSWORD) to sign and verify release EXEs."
}

# 6. Output Summary
Write-Host "`n=========================================" -ForegroundColor Cyan
Write-Host " Release Packaging Finished Successfully! " -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan

Get-ChildItem $DistDir -File | ForEach-Object {
    $sizeMb = [math]::Round($_.Length / 1MB, 2)
    Write-Host " - $($_.Name) ($sizeMb MB)" -ForegroundColor White
}

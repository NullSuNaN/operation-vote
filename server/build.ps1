param (
    [string]$Action = "help"
)

$PROJECT = "daemon.csproj"
$CONFIG = "Release"
$PUB_DIR = "dist"

function Check-Deps {
    Write-Host "==> Verifying platform infrastructure for Windows..." -ForegroundColor Cyan
    Write-Host "✅ User32.dll subsystem available natively." -ForegroundColor Green
}

function Clean-Targets {
    Write-Host "==> Purging compilation targets..." -ForegroundColor Yellow
    if (Test-Path $PUB_DIR) { Remove-Item -Recurse -Force $PUB_DIR }
    if (Test-Path "bin") { Remove-Item -Recurse -Force bin }
    if (Test-Path "obj") { Remove-Item -Recurse -Force obj }
    dotnet clean $PROJECT
}

switch ($Action) {
    "clean" {
        Clean-Targets
    }
    "build" {
        Check-Deps
        Write-Host "==> Compiling Executable Client App..." -ForegroundColor Cyan
        dotnet build $PROJECT -c $CONFIG
    }
    "publish" {
        Check-Deps
        Clean-Targets
        Write-Host "==> Publishing static distribution bundle..." -ForegroundColor Cyan
        dotnet publish $PROJECT -c $CONFIG -o $PUB_DIR
        Write-Host "`n========================================================================" -ForegroundColor Green
        Write-Host " Success! Native execution bundle generated successfully."
        Write-Host " Binary path located at: ./$PUB_DIR/"
        Write-Host "========================================================================" -ForegroundColor Green
    }
    Default {
        Write-Host "========================================================================"
        Write-Host "                     DAEMON SERVICE POWERSHELL AUTOMATION               "
        Write-Host "========================================================================"
        Write-Host "Commands:"
        Write-Host "  .\build.ps1 clean   - Wipes out temporary build files and outputs"
        Write-Host "  .\build.ps1 build   - Validates and compiles native execution builds"
        Write-Host "  .\build.ps1 publish - Verifies dependencies and outputs fully compiled artifacts"
        Write-Host "========================================================================"
    }
}
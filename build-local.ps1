# Local build & test script for Jellyfin.Plugin.Emby using Docker dotnet 10 SDK
$ErrorActionPreference = "Stop"

Write-Host "🐳 Running local build via mcr.microsoft.com/dotnet/sdk:10.0..." -ForegroundColor Cyan

docker run --rm `
    -v "${PWD}:/src" `
    -w /src `
    mcr.microsoft.com/dotnet/sdk:10.0 `
    bash -c "dotnet restore && dotnet build -c Release && dotnet run --project Jellyfin.Plugin.Emby.Tests -c Release --no-build"

if ($LASTEXITCODE -eq 0) {
    Write-Host "✨ Local build & tests completed successfully!" -ForegroundColor Green
} else {
    Write-Host "❌ Build or tests failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

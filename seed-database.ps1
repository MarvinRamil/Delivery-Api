Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Database Seeding Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "This script will seed the database with:" -ForegroundColor Yellow
Write-Host "  - Roles (SuperAdmin, Admin, etc.)" -ForegroundColor Gray
Write-Host "  - SuperAdmin user (superadmin@ilocosscript.com)" -ForegroundColor Gray
Write-Host "  - Backoffice Admin user (backoffice@ilocosscript.com)" -ForegroundColor Gray
Write-Host "  - FAQ Articles and Categories" -ForegroundColor Gray
Write-Host "  - Truck Types" -ForegroundColor Gray
Write-Host ""
Write-Host "NOTE: Make sure Seed:DefaultPassword is set in appsettings.json or environment variable" -ForegroundColor Yellow
Write-Host ""

$response = Read-Host "Press Enter to continue or Ctrl+C to cancel"

Write-Host ""
Write-Host "Starting database seeding..." -ForegroundColor Green
Write-Host ""

# Run the application in seed-only mode
dotnet run --project src/BeeLogistics.Api -- --seed-only

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "Seeding completed successfully!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Red
    Write-Host "Seeding failed! Check the logs above." -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Red
    exit $LASTEXITCODE
}

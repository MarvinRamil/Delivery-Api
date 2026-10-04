@echo off
echo ========================================
echo Database Seeding Script
echo ========================================
echo.
echo This script will seed the database with:
echo   - Roles (SuperAdmin, Admin, etc.)
echo   - SuperAdmin user (superadmin@ilocosscript.com)
echo   - Backoffice Admin user (backoffice@ilocosscript.com)
echo   - FAQ Articles and Categories
echo   - Truck Types
echo.
echo NOTE: Make sure Seed:DefaultPassword is set in appsettings.json or environment variable
echo.
pause

echo.
echo Starting database seeding...
echo.

REM Run the application in seed-only mode
dotnet run --project src/BeeLogistics.Api -- --seed-only

echo.
echo ========================================
echo Seeding completed!
echo ========================================
pause

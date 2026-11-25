@echo off
rem Checks, then builds two products from the same sources:
rem   release\HD2QD.LobbyBrowser.dll - a plugin of HD2 Mod Quick Deploy (its Plugins page)
rem   release\HD2QD.LobbyBrowser.exe - a standalone single-file program, no HD2QD needed
rem Needs the .NET SDK (8 or later): dotnet on PATH, or set DOTNET to its dotnet.exe.
rem Verify.exe --offline checks the contract; without --offline it also runs one live scan, which
rem needs Steam signed in and Helldivers 2 installed.
cd /d "%~dp0"
if "%DOTNET%"=="" set DOTNET=dotnet
"%DOTNET%" build src\HD2QD.LobbyBrowser.csproj -c Release || exit /b 1
if not exist release mkdir release
copy /y "src\bin\Release\net48\HD2QD.LobbyBrowser.dll" "release\HD2QD.LobbyBrowser.dll" >nul || exit /b 1
"%DOTNET%" build standalone\HD2QD.LobbyBrowser.exe.csproj -c Release || exit /b 1
copy /y "standalone\bin\Release\net48\HD2QD.LobbyBrowser.exe" "release\HD2QD.LobbyBrowser.exe" >nul || exit /b 1
"%DOTNET%" build tests\Verify.csproj -c Release || exit /b 1
"tests\bin\Release\net48\Verify.exe" "release\HD2QD.LobbyBrowser.dll" --offline || exit /b 1
echo.
echo Built: release\HD2QD.LobbyBrowser.dll
echo Built: release\HD2QD.LobbyBrowser.exe

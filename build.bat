@echo off
rem Checks, then builds release\HD2QD.LobbyBrowser.dll, a plugin of HD2 Mod Quick Deploy (added
rem on its Plugins page). Needs the .NET SDK (8 or later): dotnet on PATH, or set DOTNET to its
rem dotnet.exe. Verify.exe --offline checks the contract; without --offline it also runs one live
rem scan, which needs Steam signed in and Helldivers 2 installed.
cd /d "%~dp0"
if "%DOTNET%"=="" set DOTNET=dotnet
"%DOTNET%" build src\HD2QD.LobbyBrowser.csproj -c Release || exit /b 1
if not exist release mkdir release
copy /y "src\bin\Release\net48\HD2QD.LobbyBrowser.dll" "release\HD2QD.LobbyBrowser.dll" >nul || exit /b 1
"%DOTNET%" build tests\Verify.csproj -c Release || exit /b 1
"tests\bin\Release\net48\Verify.exe" "release\HD2QD.LobbyBrowser.dll" --offline || exit /b 1
echo.
echo Built: release\HD2QD.LobbyBrowser.dll

@echo off
setlocal
pushd "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Install the .NET 10 SDK, then run build.bat again.
  popd
  exit /b 1
)
dotnet publish AstraLoader.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
set "BUILD_RESULT=%ERRORLEVEL%"
if "%BUILD_RESULT%"=="0" echo Built: %CD%\publish\AstraLoader.exe
popd
exit /b %BUILD_RESULT%

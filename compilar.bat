@echo off
REM Requiere el SDK de .NET 8:  https://dotnet.microsoft.com/download/dotnet/8.0
cd /d "%~dp0"

echo === Compilando CompuHelp Remoto (cliente Windows) ===
dotnet publish Cliente\CompuHelpRemoto.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o salida\cliente
if errorlevel 1 goto error

echo === Compilando servidor de enlace (Windows) ===
dotnet publish Relay\RelayServer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o salida\relay-windows
if errorlevel 1 goto error

echo === Compilando servidor de enlace (Linux, para VPS) ===
dotnet publish Relay\RelayServer.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o salida\relay-linux
if errorlevel 1 goto error

echo.
echo Listo. Programa:  salida\cliente\CompuHelpRemoto.exe
pause
exit /b 0

:error
echo.
echo Hubo un error al compilar. Revisa los mensajes de arriba.
pause
exit /b 1

@echo off
rem Build DeltaBar plugin DLL.
rem Requires Zeepkist installed at the default Steam path with BepInEx + ZeepSDK present.
rem Game must be fully closed before running (BepInEx locks the DLL otherwise).

cd /d "%~dp0"
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set GAME=C:\Program Files (x86)\Steam\steamapps\common\Zeepkist
set MANAGED=%GAME%\Zeepkist_Data\Managed
set BEPINEX=%GAME%\BepInEx\core
set PLUGIN_DIR=%GAME%\BepInEx\plugins\DeltaBar

if not exist bin mkdir bin

"%CSC%" ^
  -target:library ^
  -noconfig ^
  -nostdlib ^
  -out:bin\DeltaBar.dll ^
  -reference:"%MANAGED%\mscorlib.dll" ^
  -reference:"%MANAGED%\netstandard.dll" ^
  -reference:"%MANAGED%\System.dll" ^
  -reference:"%MANAGED%\System.Core.dll" ^
  -reference:"%BEPINEX%\BepInEx.dll" ^
  -reference:"%BEPINEX%\0Harmony.dll" ^
  -reference:"%MANAGED%\UnityEngine.dll" ^
  -reference:"%MANAGED%\UnityEngine.CoreModule.dll" ^
  -reference:"%MANAGED%\UnityEngine.IMGUIModule.dll" ^
  -reference:"%MANAGED%\UnityEngine.InputLegacyModule.dll" ^
  -reference:"%MANAGED%\UnityEngine.UIModule.dll" ^
  -reference:"%MANAGED%\UnityEngine.TextRenderingModule.dll" ^
  -reference:"%MANAGED%\ZeepkistNetworking.dll" ^
  -reference:"%MANAGED%\Zeepkist.dll" ^
  -reference:"%MANAGED%\Facepunch.Steamworks.dll" ^
  -optimize ^
  src\Plugin.cs

if errorlevel 1 (
    echo Build failed.
    exit /b 1
)

echo Built bin\DeltaBar.dll

if not exist "%PLUGIN_DIR%" mkdir "%PLUGIN_DIR%"
xcopy /Y bin\DeltaBar.dll "%PLUGIN_DIR%\" >nul
if errorlevel 1 (
    echo Copy failed. Is the game running?
    exit /b 1
)
echo Copied to %PLUGIN_DIR%

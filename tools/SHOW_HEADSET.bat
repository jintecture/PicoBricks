@echo off
REM Shows what the headset user sees, live, in a window on the laptop (scrcpy, free/open source:
REM github.com/Genymobile/scrcpy). Nothing is installed on the headset.
REM Needs scrcpy unpacked in Downloads\scrcpy (see docs/DEVLOG.md, 23 Oct).
REM Uses Unity's adb, so scrcpy and Unity don't fight over the connection.
set "ADB=C:\Program Files\Unity\Hub\Editor\6000.3.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
set "SCRCPY=%USERPROFILE%\Downloads\scrcpy\scrcpy-win64-v5.0.1\scrcpy.exe"
if not exist "%SCRCPY%" (echo scrcpy not found at %SCRCPY% & pause & exit /b)

echo.
echo ============ Headset view ============
echo  1  Over the cable
echo  2  Wireless over the laptop hotspot (start with the cable in, then unplug it)
set /p C=Choice:
if "%C%"=="2" (
  "%SCRCPY%" --tcpip --no-audio --window-title "Headset view"
) else (
  "%SCRCPY%" --no-audio --window-title "Headset view"
)
pause

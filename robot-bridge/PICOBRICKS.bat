@echo off
REM PicoBricks -> RoboDK -> UR5.  Double-click, pick a number.
cd /d "%~dp0"
set PY=
REM RoboDK installs its own Python (with the robodk library) - use that first, so no separate Python is needed.
REM (A plain "python" on Windows can be the Microsoft Store placeholder, which doesn't run scripts.)
if exist "C:\RoboDK\Python-Embedded\python.exe" set PY="C:\RoboDK\Python-Embedded\python.exe"
if not defined PY where py >nul 2>nul && set PY=py -3
if not defined PY where python >nul 2>nul && set PY=python
if not defined PY (echo No Python found. Install it from python.org and tick "Add to PATH". & pause & exit /b)

%PY% -c "import robodk" 2>nul || (echo Installing the RoboDK Python API... & %PY% -m pip install robodk)
if not exist calib.json (copy calib.example.json calib.json >nul & echo Using the PLACEHOLDER calib.json - fine for the simulation, redo with option 5 at the robot.)

:menu
echo.
echo ================= PicoBricks bridge =================
echo  1  Sample wall in RoboDK simulation        (needs a RoboDK licence)
echo  2  Listen for headset, PRINT only           (test Wi-Fi)
echo  3  Listen for headset, GHOST ROBOT in the headset (no RoboDK)
echo  4  Replay last headset layout in RoboDK   (needs a RoboDK licence)
echo  ---------------- at the robot ----------------
echo  5  Calibrate: type the 2 corners from the pendant
echo  6  Hover over both corners, REAL robot      (check calib)
echo  7  Listen for headset, REAL robot via RoboDK
echo  8  Listen for headset, REAL robot via URScript (no RoboDK)
echo  q  Quit
set /p C=Choice: 
if "%C%"=="1" %PY% picobricks_bridge.py run --file sample_layout.json
if "%C%"=="2" %PY% picobricks_bridge.py run --backend print
if "%C%"=="3" %PY% picobricks_bridge.py run --backend ghost
if "%C%"=="4" %PY% picobricks_bridge.py run --file last_layout.json
if "%C%"=="5" %PY% picobricks_bridge.py calib
if "%C%"=="6" %PY% picobricks_bridge.py corners --real
if "%C%"=="7" %PY% picobricks_bridge.py run --real
if "%C%"=="8" %PY% picobricks_bridge.py run --backend urscript --real
if /i "%C%"=="q" exit /b
goto menu

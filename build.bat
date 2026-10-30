@echo off
rem Builds TextOutline.dll for AutoCAD 2020 with the C# compiler that ships with Windows (.NET Framework 4.x).
rem Usage:  build.bat [folder containing AcDbMgd.dll, AcCoreMgd.dll, AcMgd.dll]
rem Default folder is the AutoCAD 2020 install folder.

setlocal
set ACAD=%~1
if "%ACAD%"=="" set ACAD=C:\Program Files\Autodesk\AutoCAD 2020
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set OUT=%~dp0bin

if not exist "%ACAD%\AcDbMgd.dll" (
  echo AcDbMgd.dll not found in "%ACAD%". Pass the AutoCAD folder as the first argument.
  exit /b 1
)
if not exist "%OUT%" mkdir "%OUT%"

"%CSC%" /nologo /target:library /platform:x64 /optimize+ /out:"%OUT%\TextOutline.dll" ^
  /reference:"%ACAD%\AcDbMgd.dll" /reference:"%ACAD%\AcCoreMgd.dll" /reference:"%ACAD%\AcMgd.dll" ^
  /reference:System.Drawing.dll ^
  "%~dp0src\*.cs"
exit /b %ERRORLEVEL%

@echo off
rem Builds SnapMeasure.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No SDK, no admin rights, no installer needed.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (echo C# compiler not found & exit /b 1)

set REFS=/r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll
set OPTS=/nologo /target:winexe /optimize+ /highentropyva+ /platform:anycpu /codepage:65001 /win32manifest:app.manifest

if not exist app.ico (
  "%CSC%" %OPTS% %REFS% /out:SnapMeasure.exe src\*.cs || exit /b 1
  .\SnapMeasure.exe --write-icon app.ico
)
"%CSC%" %OPTS% /win32icon:app.ico %REFS% /out:SnapMeasure.exe src\*.cs || exit /b 1
echo Built %CD%\SnapMeasure.exe

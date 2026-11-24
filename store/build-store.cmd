@echo off
rem Builds the Microsoft Store package: Share\Store\SnapMeasure-<version>-x64.msix
rem Uses the MSIX writer built into Windows - no Windows SDK, no admin rights. Run build.cmd first.
setlocal
cd /d "%~dp0.."
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (echo C# compiler not found & exit /b 1)
if not exist SnapMeasure.exe (echo Build SnapMeasure.exe first with build.cmd & exit /b 1)
if not exist Share\Store mkdir Share\Store
"%CSC%" /nologo /target:exe /optimize+ /platform:x64 /r:System.Drawing.dll /r:System.Xml.dll /out:Share\Store\StorePack.exe store\StorePack.cs || exit /b 1
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "$v=(Get-Item SnapMeasure.exe).VersionInfo; '{0}.{1}.{2}.0' -f $v.FileMajorPart,$v.FileMinorPart,$v.FileBuildPart"`) do set VER=%%v
Share\Store\StorePack.exe SnapMeasure.exe store\AppxManifest.xml docs\icon.png LICENSE Share\Store\SnapMeasure-%VER%-x64.msix || exit /b 1
del Share\Store\StorePack.exe

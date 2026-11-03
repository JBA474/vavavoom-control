@echo off
rem Builds AuraBoxControl.exe with the C# compiler built into Windows (.NET Framework 4.x). No SDK needed.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
rem runs from the project folder (one up from tools\), where the exe, its settings and favorites live
cd /d "%~dp0.."
rem sources and embedded files are in src\
rem src\gallery.txt (Divoom's built-in designs) is optional: make it with tools\export_gallery.py, see README.md
set RES=/resource:src\extras.txt,extras.txt /resource:src\app.ico,app.ico
if exist src\gallery.txt set RES=%RES% /resource:src\gallery.txt,gallery.txt
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:AuraBoxControl.exe /win32icon:src\app.ico ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  %RES% ^
  src\AuraBoxControl.cs src\UiKit.cs src\MainForm.cs src\ImportDialog.cs

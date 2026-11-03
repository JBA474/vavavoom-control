@echo off
rem Builds AuraBoxControl.exe with the C# compiler built into Windows (.NET Framework 4.x). No SDK needed.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
cd /d "%~dp0"
rem gallery.txt (Divoom's built-in designs) is optional: make it with tools\export_gallery.py, see README.md
set RES=/resource:extras.txt,extras.txt /resource:app.ico,app.ico
if exist gallery.txt set RES=%RES% /resource:gallery.txt,gallery.txt
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:AuraBoxControl.exe /win32icon:app.ico ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  %RES% ^
  AuraBoxControl.cs UiKit.cs MainForm.cs ImportDialog.cs

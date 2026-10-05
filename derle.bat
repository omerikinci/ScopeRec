@echo off
rem Kaynak kodu degistirdikten sonra iki programi yeniden derler (Windows ile gelen derleyiciyi kullanir).
cd /d "%~dp0"
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
%CSC% -nologo -o+ -out:sds.exe usb.cs cihaz.cs sds.cs
%CSC% -nologo -o+ -target:winexe -codepage:65001 -r:System.Windows.Forms.DataVisualization.dll -win32icon:ikon.ico -r:System.Management.dll -out:ScopeRec.exe ortak.cs usb.cs cihaz.cs seri.cs gui.cs seri_gui.cs
%CSC% -nologo -o+ -target:winexe -codepage:65001 -r:System.Windows.Forms.DataVisualization.dll -win32icon:ikon.ico -r:System.Management.dll -out:ScopeView.exe ortak.cs seri.cs kayit.cs
pause

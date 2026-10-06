@echo off
rem Kaynak kodu (src klasoru) degistirdikten sonra uc programi yeniden derler. Windows ile gelen derleyiciyi kullanir.
cd /d "%~dp0"
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set GUI=-nologo -o+ -target:winexe -codepage:65001 -r:System.Windows.Forms.DataVisualization.dll -r:System.Management.dll
%CSC% -nologo -o+ -codepage:65001 -out:ScopeCli.exe src\UsbTmc.cs src\Devices.cs src\ScopeCli.cs
%CSC% %GUI% -win32icon:src\ScopeRec.ico -out:ScopeRec.exe src\Common.cs src\UsbTmc.cs src\Devices.cs src\Serial.cs src\ScopeRec.cs src\ScopeRec.Serial.cs
%CSC% %GUI% -win32icon:src\ScopeView.ico -out:ScopeView.exe src\Common.cs src\Serial.cs src\ScopeView.cs
pause

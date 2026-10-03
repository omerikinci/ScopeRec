@echo off
rem Cift tiklayinca olcumleri surekli okur ve bu klasordeki olcum_TARIH_SAAT.csv dosyasina yazar.
rem Durdurmak icin Ctrl+C. Olculecek degerleri asagidaki satirdan degistirin (ornek: C1:RMS,MEAN veya C1:RMS C2:RMS).
cd /d "%~dp0"
set OLCUM=C1:RMS
for /f %%t in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set ZAMAN=%%t
sds.exe --meas %OLCUM% --csv olcum_%ZAMAN%.csv
pause

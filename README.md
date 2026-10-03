# OsiloTakip

Siglent osiloskoptan (SDS1104X-E ile denendi) USB üzerinden sürekli ölçüm okuyan Windows uygulaması.
Osiloskopla WinUSB sürücüsü üzerinden doğrudan konuşur; NI-VISA ya da USBTMC sürücüsü gerekmez.

## Kullanım

`OsiloTakip.exe` dosyasını çalıştırın, soldan kanal ve parametreleri seçip **Başlat**'a basın.

- Kanal (C1–C4) ve ölçüm seçimi: RMS, ortalama (DC), tepe-tepe, frekans, periyot vb.
- Canlı değer kutuları (min / maks / ortalama) ve canlı grafik
- Limit kontrolü: değer alt/üst limitin dışına çıkınca uyarır, olayı saatiyle ve süresiyle kaydeder
- CSV kaydı (`kayitlar` klasörüne; bilgisayarın bölge ayarıyla yazılır, Excel'de doğrudan açılır)
- Osiloskop ekran görüntüsü alma

## Gereksinimler

- Windows, .NET Framework 4 (Windows ile birlikte gelir)
- Osiloskop için WinUSB sürücüsü (Zadig ile kurulabilir)

## Komut satırı aracı

`sds.exe` aynı bağlantıyı komut satırından kullanır:

```
sds.exe                         etkileşimli SCPI terminali
sds.exe "*IDN?"                 tek sorgu
sds.exe --meas C1:RMS,PKPK      ölçümleri sürekli oku (--csv dosya ile kaydet)
sds.exe --wave C1               dalga şeklini sürekli çek
sds.exe --tcp 5025              TCP köprüsü
```

`olcum-baslat.bat` ve `terminal.bat` bu aracı çift tıklamayla başlatır.

## Derleme

`derle.bat` iki programı Windows ile gelen C# derleyicisiyle derler; başka bir kurulum gerekmez.

| Dosya | İçerik |
|---|---|
| `usb.cs` | WinUSB üzerinden USBTMC çerçevelemesi (ortak) |
| `gui.cs` | Pencereli uygulama |
| `sds.cs` | Komut satırı aracı |

## Not

Sorgular arasında en az 40 ms beklenir: SDS1104X-E, sorgular arka arkaya gelirse yeni yakalama yapamıyor
(ekran donuyor ve hep aynı değer dönüyor).

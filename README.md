# ScopeRec

Osiloskoptan USB ya da ağ üzerinden sürekli ölçüm okuyan Windows uygulaması.
Osiloskopla WinUSB sürücüsü üzerinden doğrudan konuşur; NI-VISA ya da USBTMC sürücüsü gerekmez.

## Desteklenen cihazlar

| Marka | Durum |
|---|---|
| Siglent (SDS1104X-E) | Gerçek cihazla denendi |
| Rigol, Keysight / Agilent, Tektronix | Komut setleri kılavuzlara göre yazıldı, **gerçek cihazla denenmedi** |
| Diğer markalar | Keysight tarzı genel SCPI komutları denenir |

Komut seti cihaz kimliğinden (`*IDN?`) otomatik seçilir; soldaki **Komut seti** listesinden elle de seçilebilir.
Bağlantı USB (WinUSB sürücüsüyle, USB Test & Measurement sınıfındaki ilk cihaz) ya da ağ (ham SCPI soketi, `IP:port`) olabilir.
Yeni bir marka eklemek için `cihaz.cs` içindeki `Dialect` sınıfına bir komut şablonu eklemek yeterlidir.

## Kullanım

`ScopeRec.exe` dosyasını çalıştırın, soldan kanal ve parametreleri seçip **Başlat**'a basın.

- Kanal (C1–C4) ve ölçüm seçimi: RMS, ortalama (DC), tepe-tepe, frekans, periyot vb.
- Canlı değer kutuları (min / maks / ortalama) ve canlı grafik (30 sn – 3 gün arası zaman aralığı; veri pencereden uzunsa alttaki çubukla geçmişte gezilir)
- Limit kontrolü: değer alt/üst limitin dışına çıkınca uyarır, olayı saatiyle ve süresiyle kaydeder
- CSV kaydı (varsayılan `kayitlar` klasörüne; **Kayıt yeri…** ile konum ve klasör adı test başlamadan seçilebilir)
- Osiloskop ekran görüntüsü alma (yalnızca Siglent)
- Türkçe / İngilizce arayüz ve açık / koyu tema (üst çubuktaki listelerden)
- Grafiği temizleme (onay sorar; CSV kayıtlarına dokunmaz) ve tüm ayarları varsayılana döndürme
- Grafikte fare tekerleğiyle zamanda, Ctrl+tekerlekle değer ekseninde yakınlaştırma; çift tık sıfırlar. Fare altındaki anın değeri imlecin yanında görünür

## Kayıt görüntüleyici (ScopeView)

`ScopeView.exe` önceden alınmış kayıtları inceler. Ölçüm dosyasını (`olcum_….csv`) ve log dosyasını (`…_log.txt` ya da `…_olaylar.csv`)
pencereye sürükleyip bırakın; yalnızca ölçüm dosyası bırakılırsa yanındaki log kendiliğinden açılır.

- Tüm kaydın grafiği; fare tekerleğiyle zamanda, Ctrl+tekerlekle değer ekseninde yakınlaştırma, sürükleyerek aralık seçme, alttaki çubukla gezme
- Olay listesi: limit dışı aralıklar, iletişim hataları, bağlantı kopmaları, veri kesintileri; olaya tıklayınca grafik oraya gider
- Limit dışı aralıklar grafikte kırmızı şeritle gösterilir
- Limit analizi: logu olmayan eski kayıtlarda da alt/üst limit girip limit dışı aralıkları bulur
- Zaman ekseni kaydın kendi süresini gösterir (00:00:00 kaydın başı); üstteki listeden gerçek saate çevrilebilir
- Fare altındaki anın kayıt süresi, saati ve değerleri durum çubuğunda

ScopeRec kayıt sırasında `…_log.txt` dosyasını da yazar (zaman, t, mesaj; sekmeyle ayrık).

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
| `cihaz.cs` | Bağlantı arayüzü, ağ bağlantısı ve marka bazlı komut setleri |
| `ortak.cs` | Dil, tema ve sayı biçimlendirme (iki uygulama için ortak) |
| `gui.cs` | ScopeRec: ölçüm uygulaması |
| `kayit.cs` | ScopeView: kayıt görüntüleyici |
| `sds.cs` | Komut satırı aracı |

## Not

Sorgular arasında en az 40 ms beklenir: SDS1104X-E, sorgular arka arkaya gelirse yeni yakalama yapamıyor
(ekran donuyor ve hep aynı değer dönüyor).

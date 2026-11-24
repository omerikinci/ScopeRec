# ScopeRec Mac

Bu klasör macOS için sıfırdan yazılmış ScopeRec uygulamasını içerir. Windows Forms / WinUSB yerine PySide6 tabanlı yerel macOS arayüzü ve LAN/SCPI bağlantısı kullanır.

## Çalıştırma

```bash
./mac_app/run-mac.sh
```

Üst satırdaki **Osiloskopa bağlan** cihaz kimliğini okuyup bağlantıyı açar. **Başlat** bu bağlantıdan canlı ölçümü başlatır. **Bağlantıyı kes** bağlantıyı kapatır. Tema ve okuma aralığı ikinci satırdadır.

## Paketleme

```bash
./mac_app/build-mac-app.sh
open dist/ScopeRec.app
```

## Desteklenen ana özellikler

- Osiloskoba `IP` veya `IP:port` ile LAN/SCPI bağlantısı
- USB Test & Measurement (USBTMC) cihazları için USB kablo bağlantısı denemesi
- Siglent, Rigol, Keysight/Agilent, Tektronix ve genel SCPI ölçüm komut setleri
- C1-C4 kanal seçimi, RMS/MEAN/PKPK/FREQ/PER ve diğer ölçüm parametreleri
- Canlı grafik, canlı son/min/maks/ortalama tablosu
- Grafik üzerinde kanal/ölçüm adıyla renkli lejant; kutucuklarla çizgileri ayrı ayrı gösterme/gizleme ve tabloda eşleşen renkler
- Canlı ölçümü veya dosya kaydını durdurmadan grafiği temizleme; her seri terminal için bağımsız ekran temizleme
- Geniş tutma alanı olan panel ayırıcıları ve renkli zaman aralığı alanı
- Duraklat/devam et sırasında aynı grafik, zaman ekseni ve kayıt dosyasını koruma
- CSV ölçüm kaydı, log dosyası, olay dosyası
- Limit kontrolü, sesli uyarı, limit dışı ve normale dönüş olayları
- İşaret koyma ve grafikte işaret çizgisi
- Siglent osiloskop ekran görüntüsü kaydı (`SCDP`)
- Grafik PNG çıktısı
- Üç ayrı seri port veya `tcp://adres:port` seri köprü paneli
- Seri satırlardan `ad=değer`, `ad:değer` ve düz sayısal alan ayrıştırma
- Kayıt görüntüleyici: CSV ve yanındaki log dosyasını açıp grafikte gösterme

USB bağlantısı macOS'ta libusb/PyUSB üzerinden denenir. Cihaz USB Test & Measurement sınıfı olarak görünmüyorsa, macOS erişimi engelliyorsa veya üretici özel sürücü istiyorsa LAN/SCPI bağlantısı daha güvenilir yoldur.

Duraklatma, testi bitirme ve uygulamadan çıkış sırasında ölçüm iş parçacığının bitmesi beklenir. Yarım kalmış USB aktarımı temizlenir; destekleyen USB488 cihazlarında yerel kontrol geri verilip USB arayüzü bırakılır. Güç düğmesiyle kapatmadan önce ölçümü durdurun veya bağlantıyı kesin.

Gerçek USB cihazıyla kanal/ölçüm/renk eşleşmesini kontrol etmek için, uygulama bağlantısı kapalıyken `.venv-macapp/bin/python mac_app/check_hardware_colors.py f4ec:ee38` çalıştırılabilir. Bu kontrol C1/C2 üzerindeki RMS, MEAN ve PKPK cevaplarını grafik, lejant ve tabloyla karşılaştırır; ölçüm sürerken grafik temizlemeyi de dener. Kayıt dosyası oluşturmaz ve cihazın kanal ayarlarını değiştirmez.

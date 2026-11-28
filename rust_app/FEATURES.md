# ScopeRec Rust özellik eşleştirmesi

Bu uygulama mevcut ScopeRec'i değiştirmez. Ayrı uygulama adı, `.scoperec_rust.json` ayar dosyası ve `Documents/ScopeRec Rust` kayıt klasörü kullanır.

Rust sürümünde uygulanan özellikler:

- LAN ve USBTMC bağlantı, açık bağlan/kes, otomatik/elle komut seti, tekrar bağlanma, kesilebilir I/O ve USB488 yerel kontrolü.
- Siglent, Rigol, Keysight/Agilent, Tektronix, genel SCPI; dört kanal ve 17 ölçüm.
- Başlat, duraklat/devam, bitir, aynı kayıt/zaman ekseni, özet, istatistik sıfırlama, bağımsız grafik temizleme.
- Min/maks/ortalama, eşleşen renk/isim/birim, görünürlük seçimi, akıcı sürükleme/yakınlaştırma, tüm zaman, otomatik Y, canlı takip, imleç bilgisi.
- CSV, log, olay kayıtları; test adı, kayıt klasörü, limit ve sesli uyarı, işaret/not, osiloskop ekranı ve grafik PNG.
- Üç seri terminal; gerçek port veya TCP köprüsü, DTR/RTS, baud, satır sonu, gönderme, zaman damgası, ayrı dosyalar, tekrar bağlanma, bağımsız temizleme.
- Seri değer ayrıştırma ve görünürlük seçimi; canlı ölçümle ortak zaman, sağ eksen ve yeniden boyutlandırılabilir sağ panel/sekmeler.
- CSV/log/olay/seri dosyalarını açma, sürükle-bırak, olaylara gitme, limit analizi, veri kesintileri, gerçek saat/süre ekseni ve PNG.
- Türkçe/İngilizce, açık/koyu mat tema, ayar kalıcılığı, varsayılanları yükleme, uyku önleme, kontrollü kapanış.
- SCPI komut satırı, çok kanallı sürekli ölçüm/CSV, tekrarlı sorgu, sürekli ham dalga ve kare/özet CSV, zaman aşımı, Ctrl+C ile kaynak bırakma, seri/TCP köprüsü ve köprü üzerinden akış komutları.
- Bağımsız sanal SCPI sunucusu; uygulama içinde simülatör ve sanal seri veri kaynağı; fiziksel donanım gerektirmeyen hata/yeniden bağlanma testleri.

Doğrulama sonuçları ve fiziksel cihazla denenmeyen kısımlar README'de açıkça belirtilir. Bu geliştirmede kullanıcı talebi gereği fiziksel osiloskop kullanılmaz.

## Cihaza bağlı sınırlar

- Osiloskop ekran görüntüsü Siglent `SCDP` komutunu kullanır. Diğer markalarda anlaşılır hata gösterilir.
- Marka/modelin desteklemediği ölçümler çizgi ve istatistiğe geçerli bir sayı olarak eklenmez; `—` gösterilir. Örneğin genel SCPI şablonunda çevrim ortalaması bulunmaz.
- USB bağlantısı USB Test & Measurement sınıfını kullanır. Fiziksel cihaz ve işletim sistemi sürücüsü uyumluluğu bu sanal testlerle doğrulanmış sayılmaz.
- Osiloskobun güç düğmesine ilişkin eski sorunu önlemek için bitir/duraklat/bağlantıyı kes/kapanış işlemleri cihaz bağlantısını bırakır. Yazılımdan güç kapatma, cihazdan bağımsız güvenilir bir SCPI özelliği olarak sunulmaz.
- Grafik geçmişi bellekte seri başına sınırlıdır; CSV kaydı bu grafik sınırından etkilenmez. Büyük geçmişlerde ekrana yalnızca görünür aralığın min/maks zarfı çizilir.

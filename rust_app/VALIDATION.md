# Rust sürümü doğrulaması — 28 Kasım 2026

Geliştirme ve test sırasında fiziksel osiloskop kullanılmadı. Testler macOS 15.7.9 / Apple Silicon üzerinde Rust 1.100.1 ile çalıştırıldı.

## Otomatik kontroller

- `cargo test --all-targets --locked`: 21 test geçti (12 kütüphane, 1 komut satırı, 8 entegrasyon).
- `cargo clippy --all-targets --locked -- -D warnings`: uyarısız.
- `cargo fmt --check`: geçti.
- Bağımsız TCP simülatörüyle marka komutları, 17 Siglent ölçümü, tanımsız değerler, ikili BMP/dalga verisi, gecikmede iptal, bağlantı kaybında yeniden bağlanma ve üç seri oturum sınandı.
- Duraklat/devam işleminin aynı CSV'yi sürdürmesi, limit/işaret olayları, eski kayıt biçimleri, üç seri yan dosyası ve PNG dışa aktarma denetlendi.
- Kanal ve ölçüm anahtarlarının 68 farklı, sabit renkle eşleştiği kontrol edildi.
- 150.000 noktalı grafik, sağ seri ekseni ve imleç bilgisi egui çizim testiyle sınandı.

## Paketlenmiş uygulama

600,14 saniyelik sanal dayanıklılık testi **PASS** ile tamamlandı: 11.232 ölçüm, 57.251 arayüz geçişi, 20.804 sentetik imleç hareketi ve üç eşzamanlı seri oturum. Dokuz duraklat/devam döngüsü, kayıt sırasında grafik/terminal temizleme, limit değişiklikleri ve panel geçişleri uygulandı. CSV'deki C1 RMS satır sayısı ölçüm sayısıyla birebir eşleşti; altı osiloskop ve dokuz seri sütunu yeniden yüklendi, limit analizi ve PNG üretimi geçti. İletişim hatası oluşmadı.

Bu stres testinde macOS `time -l` en yüksek RSS değerini 161.087.488 bayt (153,6 MiB), en yüksek bellek ayak izini 146.606.848 bayt (139,8 MiB) raporladı. Bunlar bu makinedeki tek testin ölçümleridir; bellek sızıntısının her koşulda bulunmadığı garantisi değildir.

90 saniyelik sanal arayüz denemesi 1.914 ölçümle geçti. Bağımsız TCP SCPI sunucusu ve üç sanal seri oturum birlikte çalıştı; grafik/terminal temizleme, gezinme ve duraklat/devam uygulandı. Kaydedilen dosyalar yeniden açıldı ve PNG üretildi.

Kayıt görüntüleyicideki son düzeltmelerden sonra `/Applications/ScopeRec Rust.app` içindeki son kurulu kopya ayrıca 75,07 saniye çalıştırıldı: 1.555 ölçüm, üç seri oturum, duraklat/devam, dosyaların yeniden açılması ve PNG üretimi **PASS** ile sonuçlandı. Uygulamanın kendi çizim yüzeyinden `interface.png` alındı ve görsel olarak incelendi. İşletim sistemi pencere denetim aracı bu uygulamanın penceresini keşfedemediği için elle fareyle pencere testi yapıldığı iddia edilmez.

Ayrı komut satırı denemesinde 8 satırlık CSV ve TCP köprüsünden 2.048 baytlık dalga aktarımı doğrulandı. Dalga içindeki `0`, LF, CR ve `255` baytları korundu; sonraki kimlik sorgusu da doğru okundu.

## Büyük geçmiş

Üç seri × 1.000.000 nokta = 3.000.000 sentetik nokta; 250 ms örnek aralığıyla seri başına 69,44 saatlik geçmiş:

| İşlem | Ölçülen veri hazırlama süresi |
| --- | ---: |
| 1.000 ayrı 120 saniyelik pencere | Toplam 5,72 ms |
| Pencere başına ortalama | 0,0057 ms |
| Tüm geçmişin min/maks zarfı | 31,59 ms |

Tüm geçmiş görünümü 27.516 çizim noktasına indirildi; tepe değeri, veri kesintisi ve zaman sırası korundu. Bu süreler ekran/GPU çizimi veya işletim sistemi giriş gecikmesi içermez; başka bilgisayarlarda değişir.

## Doğrulanmayan alan

Sanal testler fiziksel USB/LAN cihaz uyumluluğunu, cihazın güç kapanışını veya tüm osiloskop modellerinin komut ayrıntılarını doğrulamaz. Windows paketi için GitHub Actions derleme/test sonucu ayrıca kontrol edilir; bu Mac üzerindeki ölçümler Windows performans sonucu değildir.

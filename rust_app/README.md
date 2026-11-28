# ScopeRec Rust

ScopeRec'in ayrı Rust uygulaması. Mevcut Windows veya Mac sürümünün yerine geçmez.
Uygulama adı **ScopeRec Rust**, ayar dosyası `~/.scoperec_rust.json`, varsayılan kayıt klasörü `~/Documents/ScopeRec Rust`.

## Çalıştırma

Rust stable araçlarıyla:

```sh
cargo run --release --manifest-path rust_app/Cargo.toml --bin scoperec-rust
```

Başlangıç bağlantısı **Sanal cihaz**dır. Fiziksel osiloskop olmadan **Başlat** ile ölçüm alınabilir. Seri terminallerde `sim://serial` kullanılır.

macOS uygulama paketi:

```sh
./rust_app/build-mac.sh
./rust_app/build-mac.sh --install
```

Kurulum `/Applications/ScopeRec Rust.app` oluşturur. Eski `/Applications/ScopeRec.app` korunur. Python, Qt veya Homebrew çalışma zamanı gerekmez. Uygulama içinde logo ve uygulama simgesi bulunur.

Windows için GitHub Actions'taki **ScopeRec Rust Build And Test** çalışmasının `ScopeRec-Rust-Windows` paketini açın ve `scoperec-rust.exe` çalıştırın. Mevcut C# uygulamasından ayrı dosyalar kullanır. Kaynaktan derleme için `cargo build --release --bins --manifest-path rust_app/Cargo.toml --locked` yeterlidir; Windows derleyici araçları kurulu olmalıdır.

Windows x64 dağıtımı C çalışma zamanını statik bağlar; ayrıca Visual C++ çalışma zamanı veya libusb DLL'si kurulması gerekmez. İş akışı üç EXE'nin gerçek içe aktarma tablosunu, x64 biçimini, GUI/konsol türünü ve simgesini denetler; sonucu paketteki `windows-package-check.json` dosyasına yazar. Bu denetimde kullanılan Python yalnızca derleme ortamındadır; uygulamanın çalışması için gerekmez.

## İlk kullanım ve grafik

1. **Sanal cihaz** seçiliyken kanalları ve ölçümleri belirleyip **Başlat** düğmesine basın. Dosya da isteniyorsa başlamadan önce **CSV kaydı** seçin.
2. **Duraklat / Devam et** aynı dosyayı ve zaman eksenini sürdürür. **Testi bitir** dosyaları tamamlar ve cihaz bağlantısını bırakır.
3. Grafiği sürükleyerek gezin, tekerlek/trackpad ile X ekseninde yakınlaşın. Ctrl/Command ile Y eksenini yakınlaştırın. Sağ tuşla alan seçin; çift tıklama veya **Tüm zaman** görünümü sıfırlar.
4. **Sonu izle**, geçmiş sürgüsü ve ileri/geri düğmeleriyle canlı ve geçmiş görünüm arasında geçin. Zaman alanına `30 sn`, `7 dk`, `1,5 saat` yazılabilir.
5. Her çizginin adının yanında rengi bulunur; görünürlük kutuları çizgiyi gizler. İmleç bilgi kutusu en yakın çizginin adını, zamanını ve birimli ham değerini gösterir. Farklı ölçüm birimleri üstteki birim seçiminden açılır; seri değerler ayrı sağ ekseni kullanır.
6. **Seri sağ panel** seçeneğiyle seri terminali canlı grafik yanında açın. Ayırıcı çevresindeki geniş tutma alanı paneli yeniden boyutlandırır. Üç oturum birbirinden bağımsızdır.
7. **Grafiği temizle** ve terminalin temizleme düğmesi yalnızca ekrandaki geçmişi temizler. Açık kayıt dosyası ve toplam istatistikler korunur.
8. **Kayıt görüntüleyici** mevcut ScopeRec CSV dosyalarını ve yanlarındaki olay/seri dosyalarını birlikte açar; olay satırına basarak ilgili zamana gidilir. Limit analizi ve kayıt istatistikleri aynı ekrandadır.

## Bağımsız sanal osiloskop

```sh
cargo run --release --manifest-path rust_app/Cargo.toml --bin scoperec-simulator -- --listen 127.0.0.1:17025 --brand siglent
```

GUI'de **LAN** ve `127.0.0.1:17025` seçilerek gerçek soket üzerinden simülatöre bağlanılır.
Diğer markalar: `rigol`, `keysight`, `tektronix`. `--delay-ms`, `--disconnect-after` ve `--invalid-every` seçenekleri hata senaryoları oluşturur. `--amplitude` ve `--frequency` sinyali ayarlar. Simülatör BMP ekran görüntüsü ve IEEE 488.2 dalga blokları da döndürür.

## Komut satırı

```sh
scoperec-rust-cli --simulate '*IDN?'
scoperec-rust-cli --lan 127.0.0.1:17025 --meas C1:RMS,PKPK C2:FREQ --interval 250 --count 20 --csv test.csv
scoperec-rust-cli --simulate --wave C1 --raw waveform.bin --count 1
scoperec-rust-cli --simulate --wave C1 --dump wave-frames --count 10 --csv wave-summary.csv
scoperec-rust-cli --simulate --loop '*IDN?' --count 5 --timeout 3000
scoperec-rust-cli --usb f4ec:ee38 --tcp 5026
scoperec-rust-cli --simulate --com COM11 --baud 115200
```

Komut verilmezse etkileşimli SCPI terminali açılır; `:quit` ile çıkılır. `!help`, `!clear`, `!meas [ms] C1:RMS,PKPK C2:FREQ`, `!loop [ms] sorgu` ve `!wave [ms] C1` komutları terminal, seri köprü ve TCP köprü üzerinde çalışır. Yeni satır/komut döngüyü durdurur; sonraki komut kaybolmaz. Ctrl+C durdurup bağlantı kaynaklarının bırakılmasını sağlar. `--count 0` sürekli kayıt, pozitif değer örnek/kare sayısıdır. Ham dalga dosyası seçilirse varsayılan tek karedir. `--dump` her kareyi kalibre edilmiş zaman/volt CSV'sine yazar; `--csv` dalga min/maks/ortalama özetini kaydeder. Ham dalga kalibrasyonu Siglent komutlarını kullanır.

`--com` sanal seri port çiftinin bir ucunu SCPI köprüsüne bağlar (macOS'ta cihaz yolu, Windows'ta COM adı); osiloskop bağlantısı ayrıca `--lan` veya `--usb` ile seçilir. TCP köprüsü yalnızca `127.0.0.1` üzerinde dinler. Bağlantı türü seçilmezse fiziksel cihaza dokunmadan simülatör kullanılır.

## Mimari

- `model`: parametreler, ayarlar, istatistikler, sınırlı bellek geçmişi ve görünür aralıkta min/maks nokta azaltma.
- `transport`: kesilebilir LAN I/O ve USBTMC çerçeveleme; kaynaklar Rust sahipliğiyle bırakılır.
- `protocol`: marka komutları ve ölçüm cevapları.
- `engine`: cihaz bağlantısı, zaman ekseni, kayıt, limit ve yaşam döngüsü; tek I/O iş parçacığı.
- `serial`: üç bağımsız seri iş parçacığı, TCP köprüsü ve sanal seri kaynakları.
- `recording`: CSV/log/olay/seri içe aktarma, analiz ve PNG.
- `gui`: native egui arayüzü; I/O iş parçacıklarıyla yalnızca mesajlaşır.

## Test

```sh
cargo test --manifest-path rust_app/Cargo.toml --all-targets --locked
cargo clippy --manifest-path rust_app/Cargo.toml --all-targets --locked -- -D warnings
cargo run --release --manifest-path rust_app/Cargo.toml --example history_benchmark --locked
"dist/ScopeRec Rust.app/Contents/MacOS/scoperec-rust" --self-test 600 /tmp/scoperec-rust-endurance
```

`--self-test` paketlenmiş uygulamanın gerçek pencere/çizim döngüsünü çalıştırır. Ayrı TCP SCPI sunucusu, iki kanal, üç ölçüm ve üç sanal seri oturumuyla CSV/olay/seri kaydı alır; gezinme/yakınlaştırma girdileri, duraklat/devam, işaret, limit değişimi ve ekran temizleme işlemlerini uygular. Sonunda dosyaları yeniden açıp satır sayılarını ve seri verilerini denetler; PNG, `progress.log` ve `result.json` üretir. Test modu kişisel ayarları değiştirmez. `--demo` da kayıtlı otomatik bağlantıları kullanmadan uygulamayı açar.

Bu geliştirme fiziksel osiloskop takılmadan yapılır. Sanal testler gerçek cihaz doğrulaması olarak sunulmaz. USBTMC paket başlıkları ve ikili veri çerçeveleri birim testleriyle kontrol edilir; fiziksel USB sürücüsü/cihaz uyumluluğu ayrıca gerçek donanım testi gerektirir.

`history_benchmark` üç milyon sentetik nokta üzerinde 1.000 farklı geçmiş penceresini ve tüm geçmişin min/maks zarfını ölçer. Tepe ve veri kesintilerinin korunmasını denetler. Sonuçlar veri hazırlama süresidir; ekran çizimi ve işletim sistemi giriş gecikmesini içermez.

Özellik kapsamı: [FEATURES.md](FEATURES.md).

Ölçülen sonuçlar ve sınırlar: [VALIDATION.md](VALIDATION.md).

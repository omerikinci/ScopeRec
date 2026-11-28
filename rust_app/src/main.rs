#![cfg_attr(target_os = "windows", windows_subsystem = "windows")]
fn main() -> eframe::Result {
    let icon = image::load_from_memory(include_bytes!("../../logo/scoperec-kare.png"))
        .expect("embedded icon")
        .to_rgba8();
    let icon = eframe::egui::IconData {
        width: icon.width(),
        height: icon.height(),
        rgba: icon.into_raw(),
    };
    let options = eframe::NativeOptions {
        viewport: eframe::egui::ViewportBuilder::default()
            .with_title("ScopeRec Rust")
            .with_app_id("com.omerikinci.ScopeRecRust")
            .with_inner_size([1500., 960.])
            .with_min_inner_size([1000., 700.])
            .with_icon(icon),
        ..Default::default()
    };
    let args: Vec<_> = std::env::args().collect();
    let test = args.iter().position(|s| s == "--self-test").map(|i| {
        let seconds = args
            .get(i + 1)
            .and_then(|s| s.parse::<f64>().ok())
            .unwrap_or(600.);
        let path = args
            .get(i + 2)
            .map(std::path::PathBuf::from)
            .unwrap_or_else(|| std::env::temp_dir().join("scoperec-rust-test"));
        (seconds, path)
    });
    let report = test.as_ref().map(|(_, p)| p.join("result.json"));
    let demo = args.iter().any(|s| s == "--demo") || test.is_some();
    eframe::run_native(
        "ScopeRec Rust",
        options,
        Box::new(move |cc| {
            let mut app = scoperec::gui::ScopeApp::new(cc, demo);
            if let Some((seconds, path)) = test {
                app.enable_self_test(seconds, path)?;
            }
            Ok(Box::new(app))
        }),
    )?;
    if let Some(report) = report
        && std::fs::read(report)
            .ok()
            .and_then(|s| serde_json::from_slice::<serde_json::Value>(&s).ok())
            .is_none_or(|v| v["status"] != "PASS")
    {
        std::process::exit(2);
    }
    Ok(())
}

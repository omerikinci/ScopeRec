fn main() {
    #[cfg(windows)]
    {
        let icon = std::path::PathBuf::from(std::env::var_os("CARGO_MANIFEST_DIR").unwrap())
            .join("../src/ScopeRec.ico");
        winres::WindowsResource::new()
            .set_icon(icon.to_str().unwrap())
            .set("ProductName", "ScopeRec Rust")
            .set("FileDescription", "ScopeRec Rust oscilloscope workstation")
            .compile()
            .expect("Windows icon resource");
    }
}

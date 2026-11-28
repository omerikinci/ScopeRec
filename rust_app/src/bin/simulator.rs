use clap::Parser;
use scoperec::{
    model::Dialect,
    simulator::{SimConfig, SimServer},
};
#[derive(Parser)]
#[command(about = "Virtual SCPI oscilloscope; no physical hardware required")]
struct Args {
    #[arg(long, default_value = "127.0.0.1:17025")]
    listen: String,
    #[arg(long, default_value = "siglent")]
    brand: String,
    #[arg(long, default_value_t = 0)]
    delay_ms: u64,
    #[arg(long, default_value_t = 0)]
    disconnect_after: usize,
    #[arg(long, default_value_t = 0)]
    invalid_every: usize,
    #[arg(long, default_value_t = 3.3)]
    amplitude: f64,
    #[arg(long, default_value_t = 1000.)]
    frequency: f64,
}
fn main() -> anyhow::Result<()> {
    let a = Args::parse();
    let brand = match a.brand.to_lowercase().as_str() {
        "siglent" => Dialect::Siglent,
        "rigol" => Dialect::Rigol,
        "keysight" | "agilent" => Dialect::Keysight,
        "tektronix" => Dialect::Tektronix,
        _ => anyhow::bail!("Unknown brand"),
    };
    let server = SimServer::start(
        &a.listen,
        SimConfig {
            brand,
            delay_ms: a.delay_ms,
            disconnect_after: a.disconnect_after,
            invalid_every: a.invalid_every,
            amplitude: a.amplitude,
            frequency: a.frequency,
        },
    )?;
    println!(
        "ScopeRec virtual oscilloscope listening at {} ({})",
        server.address,
        brand.label()
    );
    loop {
        std::thread::sleep(std::time::Duration::from_secs(1));
    }
}

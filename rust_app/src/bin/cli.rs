use clap::Parser;
use scoperec::{
    model::{Config, Connection, Dialect},
    transport,
};
use std::{
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::Duration,
};
#[path = "../cli_support.rs"]
mod support;
use support::{Session, Source};

#[derive(Parser)]
#[command(
    about = "ScopeRec Rust SCPI terminal, continuous recorder, waveform reader and TCP/serial bridge"
)]
struct Args {
    #[arg(long, alias = "addr", conflicts_with = "usb")]
    lan: Option<String>,
    #[arg(long)]
    usb: Option<String>,
    #[arg(long, conflicts_with_all=["lan","usb"])]
    simulate: bool,
    #[arg(long, num_args=0.., conflicts_with_all=["wave","query_loop","tcp","com"])]
    meas: Option<Vec<String>>,
    #[arg(long, conflicts_with_all=["query_loop","tcp","com"])]
    wave: Option<String>,
    #[arg(long="loop", conflicts_with_all=["tcp","com"])]
    query_loop: Option<String>,
    #[arg(long, conflicts_with = "com")]
    tcp: Option<u16>,
    #[arg(long)]
    com: Option<String>,
    #[arg(long, default_value_t = 115200)]
    baud: u32,
    #[arg(long, default_value_t = 250)]
    interval: u64,
    #[arg(long, default_value_t = 3000)]
    timeout: u64,
    #[arg(long)]
    csv: Option<PathBuf>,
    #[arg(long)]
    dump: Option<PathBuf>,
    #[arg(long)]
    raw: Option<PathBuf>,
    #[arg(long, default_value_t = 0)]
    count: u64,
    command: Option<String>,
}
fn main() -> anyhow::Result<()> {
    let a = Args::parse();
    let cfg = Config {
        connection: if a.lan.is_some() {
            Connection::Lan
        } else if a.usb.is_some() {
            Connection::Usb
        } else {
            Connection::Simulator
        },
        address: a.lan.unwrap_or_default(),
        usb: a.usb.unwrap_or_default(),
        ..Default::default()
    };
    let cancel = Arc::new(AtomicBool::new(false));
    let signal = cancel.clone();
    ctrlc::set_handler(move || signal.store(true, Ordering::SeqCst))?;
    let mut client = transport::open(&cfg, cancel.clone())?;
    client.set_timeout(Duration::from_millis(a.timeout.max(1)));
    client.clear()?;
    let id = client.text("*IDN?")?;
    eprintln!("{id}");
    let mut session = Session {
        client,
        dialect: Dialect::Auto.detect(&id),
        cancel,
        interval: a.interval.max(40),
        count: a.count,
        csv: a.csv,
        dump: a.dump,
        raw: a.raw,
    };
    if let Some(port) = a.tcp {
        return session.tcp(port);
    }
    if let Some(port) = a.com {
        return session.serial(&port, a.baud);
    }
    let source = if let Some(spec) = a.meas {
        Some(Source::measurements(&spec.join(" "))?)
    } else if let Some(ch) = a.wave {
        Some(Source::wave(session.client.as_mut(), &ch)?)
    } else {
        a.query_loop.map(Source::Query)
    };
    if let Some(source) = source {
        return session.run(source, &mut std::io::stdout(), &mut || false);
    }
    if let Some(command) = a.command {
        if command.contains('?') || command.eq_ignore_ascii_case("SCDP") {
            use std::io::Write;
            std::io::stdout().write_all(&session.client.query(&command)?)?;
        } else {
            session.client.write(&command)?;
        }
        return Ok(());
    }
    eprintln!(
        "SCPI terminal: !help for commands; :quit to disconnect; Enter or Ctrl+C stops a loop."
    );
    let rx = support::reader(std::io::stdin(), session.cancel.clone());
    session.shell(rx, &mut std::io::stdout())
}

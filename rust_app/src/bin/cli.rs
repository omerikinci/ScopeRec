use clap::Parser;
use scoperec::{
    model::{Config, Connection, Dialect},
    protocol, transport,
};
use std::{
    io::{BufRead, Write},
    sync::{Arc, atomic::AtomicBool},
    time::Duration,
};
#[derive(Parser)]
#[command(about = "ScopeRec Rust SCPI terminal, recorder, waveform reader and TCP bridge")]
struct Args {
    #[arg(long, conflicts_with = "usb")]
    lan: Option<String>,
    #[arg(long)]
    usb: Option<String>,
    #[arg(long, conflicts_with_all = ["lan", "usb"])]
    simulate: bool,
    #[arg(long)]
    meas: Option<String>,
    #[arg(long)]
    wave: Option<String>,
    #[arg(long)]
    tcp: Option<u16>,
    #[arg(long, default_value_t = 250)]
    interval: u64,
    #[arg(long)]
    csv: Option<std::path::PathBuf>,
    #[arg(long, default_value_t = 0)]
    count: u64,
    command: Option<String>,
}
fn main() -> anyhow::Result<()> {
    let a = Args::parse();
    let c = Config {
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
    let mut client = transport::open(&c, Arc::new(AtomicBool::new(false)))?;
    client.clear()?;
    let id = client.text("*IDN?")?;
    let dialect = Dialect::Auto.detect(&id);
    eprintln!("{id}");
    if let Some(port) = a.tcp {
        use std::io::BufReader;
        let listener = std::net::TcpListener::bind(("127.0.0.1", port))?;
        eprintln!("SCPI bridge 127.0.0.1:{port}");
        for stream in listener.incoming() {
            let mut stream = stream?;
            let reader = BufReader::new(stream.try_clone()?);
            for line in reader.lines() {
                let line = line?;
                if line.contains('?') || line == "SCDP" {
                    let data = client.query(&line)?;
                    write_reply(&mut stream, &line, &data)?;
                } else {
                    client.write(&line)?;
                }
            }
        }
        return Ok(());
    }
    if let Some(spec) = a.meas {
        let (ch, codes) = spec
            .split_once(':')
            .ok_or_else(|| anyhow::anyhow!("Use --meas C1:RMS,PKPK"))?;
        let ch = ch.trim_start_matches(['C', 'c']).parse::<usize>()?;
        let codes: Vec<_> = codes.split(',').map(|s| s.to_uppercase()).collect();
        let mut csv = if let Some(path) = a.csv {
            Some(csv::WriterBuilder::new().delimiter(b';').from_path(path)?)
        } else {
            None
        };
        if let Some(w) = csv.as_mut() {
            let mut h = vec!["zaman".to_owned(), "t[s]".to_owned()];
            h.extend(codes.iter().map(|k| format!("C{ch}_{k}")));
            w.write_record(h)?;
        }
        let start = std::time::Instant::now();
        let mut n = 0;
        loop {
            let v = protocol::measure(client.as_mut(), dialect, ch, &codes)?;
            let t = start.elapsed().as_secs_f64();
            println!(
                "{t:.3}\t{}",
                v.iter()
                    .map(|v| v.to_string())
                    .collect::<Vec<_>>()
                    .join("\t")
            );
            if let Some(w) = csv.as_mut() {
                let mut row = vec![
                    chrono::Local::now().format("%Y-%m-%d %H:%M:%S").to_string(),
                    format!("{t:.6}"),
                ];
                row.extend(v.iter().map(|v| {
                    if v.is_finite() {
                        v.to_string()
                    } else {
                        String::new()
                    }
                }));
                w.write_record(row)?;
                w.flush()?;
            }
            n += 1;
            if a.count > 0 && n >= a.count {
                break;
            }
            std::thread::sleep(Duration::from_millis(a.interval.max(40)));
        }
        return Ok(());
    }
    if let Some(ch) = a.wave {
        let data = client.query(&format!("{ch}:WF? DAT2"))?;
        if let Some(path) = a.csv {
            std::fs::write(path, &data)?;
        } else {
            println!(
                "{} waveform bytes\n{}",
                data.len(),
                data.iter()
                    .take(128)
                    .map(|v| format!("{v:02x}"))
                    .collect::<Vec<_>>()
                    .join(" ")
            );
        }
        return Ok(());
    }
    if let Some(command) = a.command {
        if command.contains('?') || command == "SCDP" {
            let data = client.query(&command)?;
            std::io::stdout().write_all(&data)?;
        } else {
            client.write(&command)?;
        }
        return Ok(());
    }
    eprintln!("SCPI terminal. Type :quit to disconnect.");
    for line in std::io::stdin().lock().lines() {
        let command = line?;
        if command == ":quit" {
            break;
        }
        if command.contains('?') || command.eq_ignore_ascii_case("SCDP") {
            let data = client.query(&command)?;
            if data.starts_with(b"BM") || data.contains(&0) {
                println!(
                    "{} binary bytes; use --wave/--csv or a command with output redirection to save",
                    data.len()
                );
            } else {
                println!("{}", String::from_utf8_lossy(&data).trim_end());
            }
        } else {
            client.write(&command)?;
        }
    }
    Ok(())
}

fn write_reply(mut stream: impl Write, command: &str, data: &[u8]) -> std::io::Result<()> {
    let command = command.to_ascii_uppercase();
    let binary = command.contains("WF?")
        || command.contains("WAV:DATA?")
        || command.contains("WAVEFORM:DATA?");
    if binary {
        let length = data.len().to_string();
        write!(stream, "#{}{length}", length.len())?;
    }
    stream.write_all(data)?;
    if binary || !data.ends_with(b"\n") {
        stream.write_all(b"\n")?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bridge_preserves_binary_newlines_and_block_length() {
        let mut output = Vec::new();
        write_reply(&mut output, "C1:WF? DAT2", &[0, 10, 13, 255, 10]).unwrap();
        assert_eq!(output, b"#15\0\n\r\xff\n\n");
        output.clear();
        write_reply(&mut output, "*IDN?", b"ScopeRec,SIM\n").unwrap();
        assert_eq!(output, b"ScopeRec,SIM\n");
    }
}

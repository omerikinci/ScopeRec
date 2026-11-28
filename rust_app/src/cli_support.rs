use anyhow::{Result, bail, ensure};
use crossbeam_channel::{Receiver, RecvTimeoutError, TryRecvError};
use scoperec::{
    model::{Dialect, unit},
    protocol,
    transport::{Cancel, Instrument},
};
use std::{
    io::{Read, Write},
    path::PathBuf,
    sync::atomic::Ordering,
    time::{Duration, Instant},
};

pub enum Source {
    Query(String),
    Measurements(Vec<(usize, Vec<String>)>),
    Wave {
        channel: String,
        scale: f64,
        offset: f64,
        rate: f64,
        frame: u64,
    },
}
fn alias(code: &str) -> String {
    match code.to_uppercase().as_str() {
        "VPP" | "PP" => "PKPK",
        "VRMS" => "RMS",
        "AVG" | "VMEAN" => "MEAN",
        "PRD" | "PERIOD" => "PER",
        "FRQ" => "FREQ",
        "VMAX" => "MAX",
        "VMIN" => "MIN",
        "VAMP" | "AMP" => "AMPL",
        _ => return code.to_uppercase(),
    }
    .into()
}
fn channel(s: &str) -> Result<usize> {
    let ch = s.trim_start_matches(['C', 'c']).parse::<usize>()?;
    ensure!((1..=4).contains(&ch), "Channel must be C1..C4");
    Ok(ch)
}
fn value(v: f64) -> String {
    if v.is_finite() {
        format!("{v:.12e}")
    } else {
        String::new()
    }
}
impl Source {
    pub fn measurements(spec: &str) -> Result<Self> {
        let mut groups: Vec<(usize, Vec<String>)> = Vec::new();
        for word in spec.split_whitespace() {
            let codes = if let Some((ch, codes)) = word.split_once(':') {
                groups.push((channel(ch)?, vec![]));
                codes
            } else if word.len() == 2 && word.to_uppercase().starts_with('C') {
                groups.push((channel(word)?, vec![]));
                ""
            } else {
                word
            };
            if groups.is_empty() {
                groups.push((1, vec![]));
            }
            groups
                .last_mut()
                .unwrap()
                .1
                .extend(codes.split(',').filter(|s| !s.is_empty()).map(alias));
        }
        if groups.is_empty() {
            groups.push((1, vec![]));
        }
        for (_, codes) in &mut groups {
            if codes.is_empty() {
                *codes = ["PKPK", "RMS", "MEAN", "FREQ", "PER"]
                    .map(String::from)
                    .to_vec();
            }
        }
        Ok(Self::Measurements(groups))
    }
    pub fn wave(client: &mut dyn Instrument, ch: &str) -> Result<Self> {
        let channel = format!("C{}", channel(ch)?);
        let scale = protocol::number(&client.text(&format!("{channel}:VDIV?"))?) / 25.;
        let offset = protocol::number(&client.text(&format!("{channel}:OFST?"))?);
        let rate = protocol::number(&client.text("SARA?")?);
        ensure!(
            scale.is_finite() && scale > 0. && offset.is_finite() && rate.is_finite() && rate > 0.,
            "Invalid Siglent waveform calibration"
        );
        Ok(Self::Wave {
            channel,
            scale,
            offset,
            rate,
            frame: 0,
        })
    }
    fn columns(&self) -> Vec<String> {
        match self {
            Self::Query(_) => vec!["response".into()],
            Self::Measurements(groups) => groups
                .iter()
                .flat_map(|(ch, codes)| {
                    codes
                        .iter()
                        .map(move |k| format!("C{ch}_{k}[{}]", unit(&format!("C{ch}_{k}"))))
                })
                .collect(),
            Self::Wave { .. } => ["points", "min[V]", "max[V]", "mean[V]"]
                .map(String::from)
                .to_vec(),
        }
    }
    fn sample(
        &mut self,
        client: &mut dyn Instrument,
        dialect: Dialect,
        dump: &Option<PathBuf>,
        raw: &Option<PathBuf>,
    ) -> Result<Vec<String>> {
        match self {
            Self::Query(q) => {
                let data = client.query(q)?;
                Ok(vec![
                    if data.iter().any(|b| *b < 32 && !b"\t\r\n".contains(b)) {
                        format!("<{} binary bytes>", data.len())
                    } else {
                        String::from_utf8_lossy(&data).trim().to_owned()
                    },
                ])
            }
            Self::Measurements(groups) => {
                let mut result = vec![];
                for (ch, codes) in groups {
                    result.extend(
                        protocol::measure(client, dialect, *ch, codes)?
                            .into_iter()
                            .map(value),
                    );
                }
                Ok(result)
            }
            Self::Wave {
                channel,
                scale,
                offset,
                rate,
                frame,
            } => {
                let bytes = client.query(&format!("{channel}:WF? DAT2"))?;
                ensure!(!bytes.is_empty(), "Empty waveform");
                *frame += 1;
                if let Some(path) = raw {
                    std::fs::write(path, &bytes)?;
                }
                let values: Vec<_> = bytes
                    .iter()
                    .map(|b| (*b as i8) as f64 * *scale - *offset)
                    .collect();
                if let Some(dir) = dump {
                    std::fs::create_dir_all(dir)?;
                    let mut out =
                        csv::Writer::from_path(dir.join(format!("{channel}_{frame:06}.csv")))?;
                    out.write_record(["t[s]", "v[V]"])?;
                    for (i, v) in values.iter().enumerate() {
                        out.write_record([value(i as f64 / *rate), value(*v)])?;
                    }
                    out.flush()?;
                }
                Ok(vec![
                    bytes.len().to_string(),
                    value(values.iter().copied().fold(f64::INFINITY, f64::min)),
                    value(values.iter().copied().fold(f64::NEG_INFINITY, f64::max)),
                    value(values.iter().sum::<f64>() / values.len() as f64),
                ])
            }
        }
    }
}
pub struct Session {
    pub client: Box<dyn Instrument>,
    pub dialect: Dialect,
    pub cancel: Cancel,
    pub interval: u64,
    pub count: u64,
    pub csv: Option<PathBuf>,
    pub dump: Option<PathBuf>,
    pub raw: Option<PathBuf>,
}
impl Session {
    pub fn run(
        &mut self,
        mut source: Source,
        out: &mut impl Write,
        stop: &mut impl FnMut() -> bool,
    ) -> Result<()> {
        let mut columns = vec!["zaman".into(), "t[s]".into()];
        columns.extend(source.columns());
        let mut csv = self
            .csv
            .as_ref()
            .map(|p| csv::WriterBuilder::new().delimiter(b';').from_path(p))
            .transpose()?;
        if let Some(w) = &mut csv {
            w.write_record(&columns)?;
            w.flush()?;
        }
        writeln!(out, "# {}", columns[1..].join("\t"))?;
        let start = Instant::now();
        let mut count = 0;
        let mut errors = 0;
        let target = if self.count == 0 && self.raw.is_some() {
            1
        } else {
            self.count
        };
        while !self.cancel.load(Ordering::Relaxed) && !stop() {
            let result = source.sample(self.client.as_mut(), self.dialect, &self.dump, &self.raw);
            let values = match result {
                Ok(v) => {
                    errors = 0;
                    v
                }
                Err(_) if self.cancel.load(Ordering::Relaxed) => break,
                Err(e) => {
                    errors += 1;
                    if errors >= 5 {
                        return Err(e);
                    }
                    writeln!(out, "# HATA / ERROR: {e}; retry {errors}/5")?;
                    self.client.clear()?;
                    continue;
                }
            };
            let t = start.elapsed().as_secs_f64();
            writeln!(out, "{t:.6}\t{}", values.join("\t"))?;
            out.flush()?;
            if let Some(w) = &mut csv {
                let mut row = vec![
                    chrono::Local::now()
                        .format("%Y-%m-%d %H:%M:%S%.3f")
                        .to_string(),
                    format!("{t:.6}"),
                ];
                row.extend(values);
                w.write_record(row)?;
                w.flush()?;
            }
            count += 1;
            if target > 0 && count >= target {
                break;
            }
            let until = Instant::now() + Duration::from_millis(self.interval.max(40));
            while Instant::now() < until && !self.cancel.load(Ordering::Relaxed) && !stop() {
                std::thread::sleep(Duration::from_millis(10));
            }
        }
        writeln!(
            out,
            "# {count} samples, {:.2} samples/s",
            count as f64 / start.elapsed().as_secs_f64().max(1e-6)
        )?;
        Ok(())
    }
    pub fn shell(&mut self, rx: Receiver<Option<String>>, out: &mut impl Write) -> Result<()> {
        let mut pending = None;
        let mut disconnected = false;
        while !disconnected && !self.cancel.load(Ordering::Relaxed) {
            let line = if let Some(s) = pending.take() {
                s
            } else {
                match rx.recv_timeout(Duration::from_millis(100)) {
                    Ok(Some(s)) => s,
                    Ok(None) | Err(RecvTimeoutError::Disconnected) => break,
                    Err(RecvTimeoutError::Timeout) => continue,
                }
            };
            let line = line.trim();
            if line.is_empty() {
                continue;
            }
            if [":quit", "!quit"].contains(&line) {
                break;
            }
            let result = (|| -> Result<()> {
                if line == "!help" {
                    writeln!(
                        out,
                        "!meas [ms] C1:RMS,PKPK C2:FREQ\n!loop [ms] *IDN?\n!wave [ms] C1\n!clear\n:quit\nEnter or Ctrl+C stops a loop."
                    )?;
                    return Ok(());
                }
                if line == "!clear" {
                    self.client.clear()?;
                    writeln!(out, "# cleared")?;
                    return Ok(());
                }
                if line.starts_with('!') {
                    let (cmd, arg) = line.split_once(' ').unwrap_or((line, ""));
                    let (first, rest) = arg.split_once(' ').unwrap_or((arg, ""));
                    let old_interval = self.interval;
                    let arg = if let Ok(ms) = first.parse::<u64>() {
                        self.interval = ms.max(40);
                        rest
                    } else {
                        arg
                    };
                    let source = match cmd {
                        "!meas" => Source::measurements(arg)?,
                        "!wave" => Source::wave(
                            self.client.as_mut(),
                            if arg.is_empty() { "C1" } else { arg },
                        )?,
                        "!loop" if !arg.is_empty() => Source::Query(arg.into()),
                        _ => bail!("Unknown command; use !help"),
                    };
                    let result = self.run(source, out, &mut || {
                        if pending.is_some() || disconnected {
                            return true;
                        }
                        match rx.try_recv() {
                            Ok(Some(s)) => pending = Some(s),
                            Ok(None) | Err(TryRecvError::Disconnected) => disconnected = true,
                            Err(TryRecvError::Empty) => {}
                        }
                        pending.is_some() || disconnected
                    });
                    self.interval = old_interval;
                    if self.cancel.swap(false, Ordering::SeqCst) {
                        self.client.clear()?;
                    }
                    result?;
                } else if line.contains('?') || line.eq_ignore_ascii_case("SCDP") {
                    let data = self.client.query(line)?;
                    write_reply(&mut *out, line, &data)?;
                } else {
                    self.client.write(line)?;
                }
                Ok(())
            })();
            if let Err(e) = result {
                writeln!(out, "# HATA / ERROR: {e:#}")?;
                let _ = self.client.clear();
            }
            out.flush()?;
        }
        Ok(())
    }
    pub fn tcp(&mut self, port: u16) -> Result<()> {
        let listener = std::net::TcpListener::bind(("127.0.0.1", port))?;
        listener.set_nonblocking(true)?;
        eprintln!("SCPI TCP bridge {}", listener.local_addr()?);
        while !self.cancel.load(Ordering::Relaxed) {
            match listener.accept() {
                Ok((mut stream, _)) => {
                    stream.set_read_timeout(Some(Duration::from_millis(100)))?;
                    stream.set_write_timeout(Some(Duration::from_secs(1)))?;
                    let stop = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
                    let rx = reader(stream.try_clone()?, stop.clone());
                    if let Err(e) = self.shell(rx, &mut stream) {
                        eprintln!("Bridge peer: {e:#}");
                    }
                    stop.store(true, Ordering::Relaxed);
                    let _ = stream.shutdown(std::net::Shutdown::Both);
                }
                Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                    std::thread::sleep(Duration::from_millis(20))
                }
                Err(e) => return Err(e.into()),
            }
        }
        Ok(())
    }
    pub fn serial(&mut self, port: &str, baud: u32) -> Result<()> {
        let mut link = serialport::new(port, baud)
            .timeout(Duration::from_millis(100))
            .open()?;
        eprintln!("SCPI serial bridge {port} at {baud} baud");
        let stop = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
        let rx = reader(link.try_clone()?, stop.clone());
        let result = self.shell(rx, &mut link);
        stop.store(true, Ordering::Relaxed);
        result
    }
}
pub fn reader(mut input: impl Read + Send + 'static, cancel: Cancel) -> Receiver<Option<String>> {
    let (tx, rx) = crossbeam_channel::unbounded();
    std::thread::spawn(move || {
        let mut line = vec![];
        let mut cr = false;
        let mut bytes = [0u8; 4096];
        while !cancel.load(Ordering::Relaxed) {
            match input.read(&mut bytes) {
                Ok(0) => break,
                Ok(n) => {
                    for b in &bytes[..n] {
                        if *b == b'\n' && cr {
                            cr = false;
                            continue;
                        }
                        cr = *b == b'\r';
                        if *b == b'\r' || *b == b'\n' {
                            if tx
                                .send(Some(String::from_utf8_lossy(&line).into_owned()))
                                .is_err()
                            {
                                return;
                            }
                            line.clear();
                        } else {
                            line.push(*b);
                            if line.len() > 65536 {
                                let _ = tx.send(Some("!invalid_oversized_line".into()));
                                return;
                            }
                        }
                    }
                }
                Err(e)
                    if matches!(
                        e.kind(),
                        std::io::ErrorKind::TimedOut | std::io::ErrorKind::WouldBlock
                    ) => {}
                Err(_) => break,
            }
        }
        if !line.is_empty() {
            let _ = tx.send(Some(String::from_utf8_lossy(&line).into_owned()));
        }
        let _ = tx.send(None);
    });
    rx
}
pub fn write_reply(mut stream: impl Write, command: &str, data: &[u8]) -> std::io::Result<()> {
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
    use scoperec::simulator::{SimClient, SimConfig};
    use std::sync::{Arc, atomic::AtomicBool};
    #[test]
    fn bridge_preserves_binary_newlines_and_block_length() {
        let mut out = vec![];
        write_reply(&mut out, "C1:WF? DAT2", &[0, 10, 13, 255, 10]).unwrap();
        assert_eq!(out, b"#15\0\n\r\xff\n\n");
    }
    #[test]
    fn multi_channel_aliases_and_waveform_dump() {
        let cancel = Arc::new(AtomicBool::new(false));
        let client = Box::new(SimClient::new(SimConfig::default(), cancel.clone()));
        let temp = tempfile::tempdir().unwrap();
        let mut session = Session {
            client,
            dialect: Dialect::Siglent,
            cancel,
            interval: 40,
            count: 2,
            csv: Some(temp.path().join("summary.csv")),
            dump: Some(temp.path().join("frames")),
            raw: None,
        };
        let source = Source::measurements("C1:VRMS,VPP C2:FREQ").unwrap();
        let mut out = vec![];
        session.run(source, &mut out, &mut || false).unwrap();
        assert!(String::from_utf8(out).unwrap().contains("C2_FREQ[Hz]"));
        let wave = Source::wave(session.client.as_mut(), "c2").unwrap();
        session.run(wave, &mut Vec::new(), &mut || false).unwrap();
        for frame in 1..=2 {
            let mut csv =
                csv::Reader::from_path(temp.path().join(format!("frames/C2_{frame:06}.csv")))
                    .unwrap();
            assert_eq!(csv.records().count(), 2048);
        }
    }
    #[test]
    fn bridge_line_reader_ignores_crlf_but_keeps_empty_stop_command() {
        let cancel = Arc::new(AtomicBool::new(false));
        let rx = reader(std::io::Cursor::new(b"*IDN?\r\n\r\n:quit\n"), cancel);
        let lines: Vec<_> = rx.iter().collect();
        assert_eq!(
            lines,
            vec![
                Some("*IDN?".into()),
                Some("".into()),
                Some(":quit".into()),
                None
            ]
        );
    }

    #[test]
    fn streaming_shell_stops_and_processes_the_next_query() {
        let cancel = Arc::new(AtomicBool::new(false));
        let (tx, rx) = crossbeam_channel::unbounded();
        tx.send(Some("!loop 40 *IDN?".into())).unwrap();
        let producer = std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(110));
            for line in ["", "*IDN?", ":quit"] {
                tx.send(Some(line.into())).unwrap();
            }
        });
        let mut session = Session {
            client: Box::new(SimClient::new(Default::default(), cancel.clone())),
            dialect: Dialect::Siglent,
            cancel,
            interval: 40,
            count: 0,
            csv: None,
            dump: None,
            raw: None,
        };
        let mut out = vec![];
        session.shell(rx, &mut out).unwrap();
        producer.join().unwrap();
        let text = String::from_utf8(out).unwrap();
        assert!(text.contains("samples/s"));
        let summary = text.find("samples/s").unwrap();
        assert!(text[summary..].contains("RUST-SIM-001"));
    }
}

use crate::{engine::SharedRun, model::SerialConfig, recording::suffix};
use anyhow::{Result, bail};
use crossbeam_channel::{Receiver, Sender, unbounded};
use std::{
    collections::BTreeMap,
    fs::{File, OpenOptions},
    io::{BufWriter, Read, Write},
    net::TcpStream,
    path::PathBuf,
    sync::{
        Arc, OnceLock,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::{Duration, Instant},
};
pub fn parse_values(text: &str) -> BTreeMap<String, f64> {
    static RE: OnceLock<regex::Regex> = OnceLock::new();
    let re = RE.get_or_init(|| {
        regex::Regex::new(
            r"([A-Za-z_][A-Za-z0-9_.-]*)\s*[:=]\s*([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)",
        )
        .unwrap()
    });
    let mut values = BTreeMap::new();
    for c in re.captures_iter(text) {
        if let Ok(v) = c[2].parse::<f64>()
            && v.is_finite()
        {
            values.insert(c[1].to_owned(), v);
        }
    }
    if values.is_empty() {
        let parts: Vec<_> = text
            .split(|c: char| c.is_whitespace() || c == ',' || c == ';')
            .filter(|s| !s.is_empty())
            .collect();
        if !parts.is_empty()
            && parts
                .iter()
                .all(|s| s.parse::<f64>().is_ok_and(|v| v.is_finite()))
        {
            for (i, v) in parts.iter().enumerate() {
                values.insert(format!("S{}", i + 1), v.parse().unwrap());
            }
        }
    }
    values
}
pub fn ports() -> Vec<String> {
    let mut out = vec!["sim://serial".into()];
    if let Ok(ports) = serialport::available_ports() {
        out.extend(ports.into_iter().map(|p| p.port_name));
    }
    out
}
pub enum SerialCommand {
    Send(String),
    Configure(SerialConfig),
    Stop,
}
#[derive(Debug)]
pub enum SerialEvent {
    Connected(usize, String),
    Line(usize, f64, String),
    Values(usize, u64, f64, BTreeMap<String, f64>),
    State(usize, String),
    Error(usize, String),
    Closed(usize),
}
pub struct SerialSession {
    pub tx: Sender<SerialCommand>,
    stop: Arc<AtomicBool>,
    thread: Option<thread::JoinHandle<()>>,
}
impl SerialSession {
    pub fn start(
        index: usize,
        config: SerialConfig,
        shared: SharedRun,
        directory: PathBuf,
        events: Sender<SerialEvent>,
    ) -> Self {
        let (tx, rx) = unbounded();
        let stop = Arc::new(AtomicBool::new(false));
        let token = stop.clone();
        let thread =
            thread::spawn(move || run(index, config, shared, directory, events, rx, token));
        Self {
            tx,
            stop,
            thread: Some(thread),
        }
    }
    pub fn send(&self, c: SerialCommand) {
        if matches!(c, SerialCommand::Stop) {
            self.stop.store(true, Ordering::Relaxed);
        }
        let _ = self.tx.send(c);
    }
    pub fn finished(&self) -> bool {
        self.thread.as_ref().is_none_or(|h| h.is_finished())
    }
}
impl Drop for SerialSession {
    fn drop(&mut self) {
        self.send(SerialCommand::Stop);
        if let Some(h) = self.thread.take() {
            let _ = h.join();
        }
    }
}
enum Link {
    Port(Box<dyn serialport::SerialPort>),
    Tcp(TcpStream),
    Sim(Instant),
}
impl Link {
    fn open(config: &SerialConfig) -> Result<Self> {
        if config.port.starts_with("sim://") {
            return Ok(Self::Sim(Instant::now()));
        }
        if let Some(address) = config.port.strip_prefix("tcp://") {
            use std::net::ToSocketAddrs;
            let addr = address
                .to_socket_addrs()?
                .next()
                .ok_or_else(|| anyhow::anyhow!("Invalid TCP serial address"))?;
            let s = TcpStream::connect_timeout(&addr, Duration::from_secs(2))?;
            s.set_read_timeout(Some(Duration::from_millis(50)))?;
            s.set_write_timeout(Some(Duration::from_millis(500)))?;
            return Ok(Self::Tcp(s));
        }
        let mut port = serialport::new(&config.port, config.baud)
            .timeout(Duration::from_millis(50))
            .flow_control(serialport::FlowControl::None)
            .open()?;
        port.write_data_terminal_ready(config.dtr)?;
        port.write_request_to_send(config.rts)?;
        Ok(Self::Port(port))
    }
    fn read(&mut self, buf: &mut [u8]) -> Result<usize> {
        match self {
            Self::Port(p) => match p.read(buf) {
                Err(e) if e.kind() == std::io::ErrorKind::TimedOut => Ok(0),
                r => Ok(r?),
            },
            Self::Tcp(s) => match s.read(buf) {
                Ok(0) => bail!("Serial bridge closed"),
                Err(e)
                    if matches!(
                        e.kind(),
                        std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut
                    ) =>
                {
                    Ok(0)
                }
                r => Ok(r?),
            },
            Self::Sim(start) => {
                thread::sleep(Duration::from_millis(80));
                let t = start.elapsed().as_secs_f64();
                let data = format!(
                    "SET:1500,ACT:{:.3},T:{:.3}\n",
                    1498. + t.sin() * 5.,
                    24. + t.sin() * 2.
                );
                let n = data.len().min(buf.len());
                buf[..n].copy_from_slice(&data.as_bytes()[..n]);
                Ok(n)
            }
        }
    }
    fn write(&mut self, data: &[u8]) -> Result<()> {
        match self {
            Self::Port(p) => p.write_all(data)?,
            Self::Tcp(s) => s.write_all(data)?,
            Self::Sim(_) => {}
        }
        Ok(())
    }
    fn configure(&mut self, c: &SerialConfig) -> Result<()> {
        if let Self::Port(p) = self {
            p.set_baud_rate(c.baud)?;
            p.write_data_terminal_ready(c.dtr)?;
            p.write_request_to_send(c.rts)?;
        }
        Ok(())
    }
}
fn run(
    index: usize,
    mut config: SerialConfig,
    shared: SharedRun,
    directory: PathBuf,
    events: Sender<SerialEvent>,
    rx: Receiver<SerialCommand>,
    stop: Arc<AtomicBool>,
) {
    let mut link = None;
    let mut next = Instant::now();
    let mut buffer = Vec::new();
    let mut writer: Option<BufWriter<File>> = None;
    let mut writer_path = None;
    let standalone = directory.join(format!(
        "seri_rust_{}_{}",
        index + 1,
        chrono::Local::now().format("%Y%m%d_%H%M%S_%3f")
    ));
    let mut flushed = Instant::now();
    while !stop.load(Ordering::Relaxed) {
        while let Ok(c) = rx.try_recv() {
            match c {
                SerialCommand::Stop => stop.store(true, Ordering::Relaxed),
                SerialCommand::Send(command) => {
                    if let Some(link) = link.as_mut() {
                        let ending = match config.eol {
                            1 => "\n",
                            2 => "\r\n",
                            3 => "\r",
                            _ => "",
                        };
                        if let Err(e) = Link::write(link, format!("{command}{ending}").as_bytes()) {
                            let _ = events.send(SerialEvent::Error(index, e.to_string()));
                        }
                    }
                }
                SerialCommand::Configure(c) => {
                    config = c;
                    if let Some(link) = link.as_mut() {
                        let _ = Link::configure(link, &config);
                    }
                }
            }
        }
        if stop.load(Ordering::Relaxed) {
            break;
        }
        if link.is_none() {
            if Instant::now() < next {
                thread::sleep(Duration::from_millis(50));
                continue;
            }
            match Link::open(&config) {
                Ok(l) => {
                    link = Some(l);
                    buffer.clear();
                    let _ = events.send(SerialEvent::Connected(index, config.port.clone()));
                }
                Err(e) => {
                    let _ = events.send(SerialEvent::Error(index, e.to_string()));
                    next = Instant::now() + Duration::from_secs(2);
                    continue;
                }
            }
        }
        let mut data = [0u8; 4096];
        match link.as_mut().unwrap().read(&mut data) {
            Ok(n) => {
                buffer.extend_from_slice(&data[..n]);
                while let Some(end) = buffer.iter().position(|b| *b == b'\n' || *b == b'\r') {
                    let bytes: Vec<_> = buffer.drain(..=end).collect();
                    let line = String::from_utf8_lossy(&bytes)
                        .trim_end_matches(['\r', '\n'])
                        .to_owned();
                    if line.is_empty() {
                        continue;
                    }
                    let values = parse_values(&line);
                    let (t, generation, stem) = {
                        let mut context = shared.lock().unwrap_or_else(|e| e.into_inner());
                        for (key, value) in &values {
                            context
                                .serial_stats
                                .entry(format!("Serial{}_{}", index + 1, key))
                                .or_default()
                                .add(*value);
                        }
                        (
                            context.origin.elapsed().as_secs_f64(),
                            context.generation,
                            context.stem.clone(),
                        )
                    };
                    let _ = events.send(SerialEvent::Line(index, t, line.clone()));
                    if !values.is_empty() {
                        let _ = events.send(SerialEvent::Values(index, generation, t, values));
                    }
                    if config.record {
                        let stem = stem.as_ref().unwrap_or(&standalone);
                        let path = suffix(
                            stem,
                            &format!(
                                "_seri{}.txt",
                                if index == 0 {
                                    String::new()
                                } else {
                                    (index + 1).to_string()
                                }
                            ),
                        );
                        if writer_path.as_ref() != Some(&path) {
                            if let Some(mut w) = writer.take() {
                                let _ = w.flush();
                            }
                            if let Some(parent) = path.parent() {
                                let _ = std::fs::create_dir_all(parent);
                            }
                            match OpenOptions::new().create(true).append(true).open(&path) {
                                Ok(file) => {
                                    writer = Some(BufWriter::new(file));
                                    writer_path = Some(path);
                                }
                                Err(e) => {
                                    config.record = false;
                                    let _ = events
                                        .send(SerialEvent::Error(index, format!("Recording: {e}")));
                                }
                            }
                        }
                        if let Some(w) = writer.as_mut() {
                            let result = writeln!(
                                w,
                                "{}\t{t:.6}\t{line}",
                                chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f")
                            );
                            if let Err(e) = result {
                                let _ = events
                                    .send(SerialEvent::Error(index, format!("Recording: {e}")));
                                config.record = false;
                            }
                        }
                    } else if let Some(mut w) = writer.take() {
                        let _ = w.flush();
                        writer_path = None;
                    }
                }
                if buffer.len() > 65536 {
                    buffer.clear();
                    let _ = events.send(SerialEvent::Error(
                        index,
                        "Unterminated serial line exceeded 64 KiB".into(),
                    ));
                }
            }
            Err(e) => {
                link = None;
                next = Instant::now() + Duration::from_secs(2);
                let _ = events.send(SerialEvent::State(
                    index,
                    format!("Yeniden bağlanıyor / Reconnecting: {e}"),
                ));
            }
        }
        if flushed.elapsed() >= Duration::from_secs(1) {
            if let Some(w) = writer.as_mut() {
                let _ = w.flush();
            }
            flushed = Instant::now();
        }
    }
    if let Some(w) = writer.as_mut() {
        let _ = w.flush();
    }
    let _ = events.send(SerialEvent::Closed(index));
}
pub fn channel() -> (Sender<SerialEvent>, Receiver<SerialEvent>) {
    unbounded()
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn named_and_plain_values() {
        let p = parse_values("SET:1500,ACT:1498,T:+45.3, V=-2.1e-3");
        assert_eq!(p["T"], 45.3);
        assert_eq!(p["V"], -0.0021);
        assert_eq!(parse_values("1.2, 2, -3")["S3"], -3.);
        assert!(parse_values("boot ready").is_empty());
    }
}

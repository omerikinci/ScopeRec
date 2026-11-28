use crate::{
    model::{Dialect, PARAMS},
    transport::{Cancel, Instrument, check_cancel},
};
use anyhow::{Result, bail};
use serde::{Deserialize, Serialize};
use std::{
    io::{BufRead, BufReader, Write},
    net::{TcpListener, TcpStream},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::{Duration, Instant},
};
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct SimConfig {
    pub brand: Dialect,
    pub delay_ms: u64,
    pub disconnect_after: usize,
    pub invalid_every: usize,
    pub amplitude: f64,
    pub frequency: f64,
}
impl Default for SimConfig {
    fn default() -> Self {
        Self {
            brand: Dialect::Siglent,
            delay_ms: 0,
            disconnect_after: 0,
            invalid_every: 0,
            amplitude: 3.3,
            frequency: 1000.,
        }
    }
}
pub struct SimClient {
    pub config: SimConfig,
    start: Instant,
    queries: usize,
    pending: String,
    cancel: Cancel,
}
impl SimClient {
    pub fn new(config: SimConfig, cancel: Cancel) -> Self {
        Self {
            config,
            start: Instant::now(),
            queries: 0,
            pending: String::new(),
            cancel,
        }
    }
    pub fn value(&self, ch: usize, code: &str) -> f64 {
        if self.config.invalid_every > 0 && self.queries.is_multiple_of(self.config.invalid_every) {
            return f64::NAN;
        }
        let a = self.config.amplitude * (1. + ch as f64 * 0.05);
        let drift = 1. + 0.02 * (self.start.elapsed().as_secs_f64() * 0.7 + ch as f64).sin();
        match code {
            "RMS" | "CRMS" => a / (2. * 2f64.sqrt()) * drift,
            "MEAN" | "CMEAN" => 0.01 * ch as f64,
            "PKPK" | "AMPL" => a * drift,
            "MAX" | "TOP" => a / 2. * drift,
            "MIN" | "BASE" => -a / 2. * drift,
            "FREQ" => self.config.frequency * drift,
            "PER" => 1. / (self.config.frequency * drift),
            "DUTY" => 50.,
            "PWID" | "NWID" => 0.5 / self.config.frequency,
            "RISE" | "FALL" => 20e-9,
            _ => f64::NAN,
        }
    }
    pub fn response(&mut self, cmd: &str) -> Result<Vec<u8>> {
        self.queries += 1;
        let c = cmd.trim().to_uppercase();
        if self.config.disconnect_after > 0 && self.queries > self.config.disconnect_after {
            bail!("simulated connection loss");
        }
        let until = Instant::now() + Duration::from_millis(self.config.delay_ms);
        while Instant::now() < until {
            check_cancel(&self.cancel)?;
            thread::sleep(Duration::from_millis(5));
        }
        check_cancel(&self.cancel)?;
        if c == "*IDN?" {
            return Ok(format!(
                "{},SCOPE-SIM,RUST-SIM-001,1.0\n",
                match self.config.brand {
                    Dialect::Rigol => "RIGOL TECHNOLOGIES",
                    Dialect::Keysight => "KEYSIGHT",
                    Dialect::Tektronix => "TEKTRONIX",
                    _ => "Siglent Technologies",
                }
            )
            .into_bytes());
        }
        let ch = c
            .strip_prefix('C')
            .and_then(|s| s.chars().next())
            .and_then(|c| c.to_digit(10))
            .or_else(|| c.rsplit_once("CHANNEL").and_then(|(_, s)| s.parse().ok()))
            .or_else(|| {
                c.split("CH")
                    .nth(1)
                    .and_then(|s| s.chars().next())
                    .and_then(|c| c.to_digit(10))
            })
            .unwrap_or(1) as usize;
        if c.contains("PAVA?") {
            let code = c.split('?').nth(1).unwrap_or("ALL").trim();
            let values = PARAMS
                .iter()
                .filter(|p| code == "ALL" || p.code == code)
                .map(|p| format!("{},{:.9e}", p.code, self.value(ch, p.code)))
                .collect::<Vec<_>>()
                .join(",");
            return Ok(format!("C{ch}:PAVA {values}\n").into_bytes());
        }
        if c == "SCDP" {
            let mut cursor = std::io::Cursor::new(Vec::new());
            let img = image::RgbImage::from_fn(640, 360, |x, y| {
                if ((x as f64 / 30.).sin() * 100. + 180. - y as f64).abs() < 2. {
                    image::Rgb([250, 180, 35])
                } else {
                    image::Rgb([12, 22, 34])
                }
            });
            image::DynamicImage::ImageRgb8(img).write_to(&mut cursor, image::ImageFormat::Bmp)?;
            return Ok(cursor.into_inner());
        }
        if c.contains("WF?") || c.contains("WAV:DATA?") {
            let mut samples: Vec<u8> = (0..2048)
                .map(|i| (127. + 100. * (i as f64 / 30.).sin()) as u8)
                .collect();
            samples[..4].copy_from_slice(&[0, 10, 13, 255]);
            let mut out =
                format!("#{}{}", samples.len().to_string().len(), samples.len()).into_bytes();
            out.extend(samples);
            return Ok(out);
        }
        if c.contains('?') {
            for p in PARAMS {
                if let Some((command, _)) = crate::protocol::command(self.config.brand, ch, p.code)
                    && command.to_uppercase() == c
                {
                    let v = self.value(ch, p.code);
                    return Ok(format!(
                        "{:.10e}\n",
                        if self.config.brand == Dialect::Rigol && p.code == "DUTY" {
                            v / 100.
                        } else {
                            v
                        }
                    )
                    .into_bytes());
                }
            }
            return Ok(b"0\n".to_vec());
        }
        Ok(Vec::new())
    }
}
impl Instrument for SimClient {
    fn write(&mut self, command: &str) -> Result<()> {
        check_cancel(&self.cancel)?;
        self.pending = command.into();
        Ok(())
    }
    fn read(&mut self) -> Result<Vec<u8>> {
        let cmd = std::mem::take(&mut self.pending);
        self.response(&cmd)
    }
    fn clear(&mut self) -> Result<()> {
        self.pending.clear();
        Ok(())
    }
}
pub struct SimServer {
    pub address: std::net::SocketAddr,
    stop: Arc<AtomicBool>,
    thread: Option<thread::JoinHandle<()>>,
}
impl SimServer {
    pub fn start(address: &str, config: SimConfig) -> Result<Self> {
        let listener = TcpListener::bind(address)?;
        let address = listener.local_addr()?;
        listener.set_nonblocking(true)?;
        let stop = Arc::new(AtomicBool::new(false));
        let stop2 = stop.clone();
        let handle = thread::spawn(move || {
            let mut clients = vec![];
            while !stop2.load(Ordering::Relaxed) {
                match listener.accept() {
                    Ok((stream, _)) => {
                        let config = config.clone();
                        let stop = stop2.clone();
                        clients.push(thread::spawn(move || {
                            if let Err(error) = serve(stream, config, stop) {
                                eprintln!("Simulator client: {error:#}");
                            }
                        }));
                    }
                    Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                        thread::sleep(Duration::from_millis(10))
                    }
                    Err(_) => break,
                }
            }
            for client in clients {
                let _ = client.join();
            }
        });
        Ok(Self {
            address,
            stop,
            thread: Some(handle),
        })
    }
}
impl Drop for SimServer {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(handle) = self.thread.take() {
            let _ = handle.join();
        }
    }
}
fn serve(mut stream: TcpStream, config: SimConfig, stop: Cancel) -> Result<()> {
    stream.set_nonblocking(false)?;
    stream.set_read_timeout(Some(Duration::from_millis(100)))?;
    stream.set_write_timeout(Some(Duration::from_secs(3)))?;
    let mut reader = BufReader::new(stream.try_clone()?);
    let mut sim = SimClient::new(config, stop.clone());
    let mut line = String::new();
    while !stop.load(Ordering::Relaxed) {
        match reader.read_line(&mut line) {
            Ok(0) => break,
            Ok(_) => {
                let bytes = sim.response(line.trim())?;
                if !bytes.is_empty() {
                    stream.write_all(&bytes)?;
                }
                line.clear();
            }
            Err(e)
                if matches!(
                    e.kind(),
                    std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut
                ) => {}
            Err(e) => return Err(e.into()),
        }
    }
    Ok(())
}

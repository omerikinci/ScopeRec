use crate::{
    model::{Config, Dialect, Event, Stats},
    recording::Recorder,
    transport::{self, Cancel, Instrument},
};
use anyhow::{Result, bail};
use crossbeam_channel::{Receiver, Sender, unbounded};
use std::{
    collections::BTreeMap,
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::{Duration, Instant},
};
#[derive(Clone)]
pub struct RunContext {
    pub origin: Instant,
    pub stem: Option<PathBuf>,
    pub generation: u64,
    pub serial_stats: BTreeMap<String, Stats>,
}
pub type SharedRun = Arc<Mutex<RunContext>>;
pub fn shared_run() -> SharedRun {
    Arc::new(Mutex::new(RunContext {
        origin: Instant::now(),
        stem: None,
        generation: 0,
        serial_stats: BTreeMap::new(),
    }))
}
pub enum Command {
    Connect(Config),
    Disconnect,
    Start(Config),
    Pause,
    End,
    Mark(String),
    ResetStats,
    Options(LiveOptions),
    Screenshot(PathBuf),
    Raw(String),
    Shutdown,
}
#[derive(Clone, Debug, PartialEq)]
pub struct LiveOptions {
    pub interval_ms: u64,
    pub limit: bool,
    pub key: String,
    pub low: Option<f64>,
    pub high: Option<f64>,
}
impl From<&Config> for LiveOptions {
    fn from(c: &Config) -> Self {
        Self {
            interval_ms: c.interval_ms,
            limit: c.limit,
            key: c.limit_series.clone(),
            low: c.low,
            high: c.high,
        }
    }
}
#[derive(Debug)]
pub enum EngineEvent {
    Connected(String, Dialect),
    State(String),
    Started {
        keys: Vec<String>,
        stem: Option<PathBuf>,
        resumed: bool,
        t: f64,
    },
    Sample {
        t: f64,
        values: Vec<(String, f64)>,
    },
    Log(Event),
    Paused,
    Ended {
        samples: u64,
        violations: u64,
    },
    Screenshot(PathBuf),
    Error(String),
    Closed,
}
pub struct Engine {
    pub tx: Sender<Command>,
    pub events: Receiver<EngineEvent>,
    pub cancel: Cancel,
    thread: Option<thread::JoinHandle<()>>,
}
impl Engine {
    pub fn new(shared: SharedRun) -> Self {
        let (tx, rx) = unbounded();
        let (etx, events) = unbounded();
        let cancel = Arc::new(AtomicBool::new(false));
        let token = cancel.clone();
        let handle = thread::spawn(move || Worker::new(rx, etx, token, shared).run());
        Self {
            tx,
            events,
            cancel,
            thread: Some(handle),
        }
    }
    pub fn send(&self, c: Command) {
        if matches!(
            c,
            Command::Pause | Command::End | Command::Disconnect | Command::Shutdown
        ) {
            self.cancel.store(true, Ordering::SeqCst);
        }
        let _ = self.tx.send(c);
    }
    pub fn finished(&self) -> bool {
        self.thread.as_ref().is_none_or(|h| h.is_finished())
    }
}
impl Drop for Engine {
    fn drop(&mut self) {
        self.send(Command::Shutdown);
        if let Some(h) = self.thread.take() {
            let _ = h.join();
        }
    }
}
struct Worker {
    rx: Receiver<Command>,
    tx: Sender<EngineEvent>,
    cancel: Cancel,
    shared: SharedRun,
    client: Option<Box<dyn Instrument>>,
    config: Config,
    dialect: Dialect,
    recorder: Option<Recorder>,
    origin: Option<Instant>,
    active: bool,
    running: bool,
    samples: u64,
    violations: u64,
    stats: BTreeMap<String, Stats>,
    alarm: Option<Instant>,
    alarm_t: f64,
    next: Instant,
    retry: usize,
}
impl Worker {
    fn new(
        rx: Receiver<Command>,
        tx: Sender<EngineEvent>,
        cancel: Cancel,
        shared: SharedRun,
    ) -> Self {
        Self {
            rx,
            tx,
            cancel,
            shared,
            client: None,
            config: Config::default(),
            dialect: Dialect::Siglent,
            recorder: None,
            origin: None,
            active: false,
            running: false,
            samples: 0,
            violations: 0,
            stats: BTreeMap::new(),
            alarm: None,
            alarm_t: 0.,
            next: Instant::now(),
            retry: 0,
        }
    }
    fn t(&self) -> f64 {
        self.origin.map_or(0., |o| o.elapsed().as_secs_f64())
    }
    fn event(&mut self, event: Event) {
        if let Some(r) = self.recorder.as_mut()
            && let Err(e) = r.event(&event)
        {
            let _ = self.tx.send(EngineEvent::Error(format!("Recording: {e}")));
        }
        let _ = self.tx.send(EngineEvent::Log(event));
    }
    fn connect(&mut self) -> Result<()> {
        self.cancel.store(false, Ordering::SeqCst);
        let _ = self
            .tx
            .send(EngineEvent::State("Bağlanıyor / Connecting".into()));
        let mut client = transport::open(&self.config, self.cancel.clone())?;
        client.clear()?;
        let id = client.text("*IDN?")?;
        if id.is_empty() {
            bail!("empty instrument identification");
        }
        self.dialect = self.config.dialect.detect(&id);
        self.client = Some(client);
        self.retry = 0;
        let _ = self
            .tx
            .send(EngineEvent::Connected(id.clone(), self.dialect));
        self.event(Event::new(
            self.t(),
            "connection",
            format!("Bağlandı / Connected: {id}"),
        ));
        Ok(())
    }
    fn end(&mut self) {
        self.running = false;
        self.client = None;
        if self.active {
            let t = self.t();
            let mut stats = self.stats.clone();
            stats.extend(
                self.shared
                    .lock()
                    .unwrap_or_else(|e| e.into_inner())
                    .serial_stats
                    .clone(),
            );
            if let Some(mut r) = self.recorder.take()
                && let Err(e) = r.summary(t, self.samples, self.violations, &stats)
            {
                let _ = self.tx.send(EngineEvent::Error(e.to_string()));
            }
            self.shared.lock().unwrap_or_else(|e| e.into_inner()).stem = None;
            self.event(Event::new(
                t,
                "summary",
                format!(
                    "Test bitti / Complete: {t:.2}s, {} readings, {} violations",
                    self.samples, self.violations
                ),
            ));
            let _ = self.tx.send(EngineEvent::Ended {
                samples: self.samples,
                violations: self.violations,
            });
        }
        self.active = false;
        self.alarm = None;
    }
    fn handle(&mut self, c: Command) -> Result<bool> {
        match c {
            Command::Connect(config) => {
                self.client = None;
                self.config = config;
                self.connect()?;
            }
            Command::Disconnect => {
                self.running = false;
                self.client = None;
                let _ = self.tx.send(EngineEvent::Paused);
            }
            Command::Start(config) => {
                if !config.channels.iter().any(|x| *x) || config.params.is_empty() {
                    bail!("Select a channel and measurement");
                }
                self.config = config;
                self.cancel.store(false, Ordering::SeqCst);
                if self.config.dialect != Dialect::Auto {
                    self.dialect = self.config.dialect;
                }
                let resumed = self.active;
                if !resumed {
                    let keys = self.config.keys();
                    let recorder = if self.config.record {
                        Some(Recorder::new(&self.config, &keys)?)
                    } else {
                        None
                    };
                    self.origin = Some(Instant::now());
                    self.samples = 0;
                    self.violations = 0;
                    self.stats = keys.iter().map(|k| (k.clone(), Stats::default())).collect();
                    self.recorder = recorder;
                    self.active = true;
                    self.alarm = None;
                    let mut shared = self.shared.lock().unwrap_or_else(|e| e.into_inner());
                    shared.origin = self.origin.unwrap();
                    shared.stem = self.recorder.as_ref().map(|r| r.stem.clone());
                    shared.generation += 1;
                    shared.serial_stats.clear();
                }
                let _ = self.tx.send(EngineEvent::Started {
                    keys: self.config.keys(),
                    stem: self.recorder.as_ref().map(|r| r.stem.clone()),
                    resumed,
                    t: self.t(),
                });
                if resumed {
                    self.event(Event::new(self.t(), "resume", "Devam / Resume"));
                }
                self.running = true;
                self.next = Instant::now();
            }
            Command::Pause => {
                self.running = false;
                self.client = None;
                if let Some(r) = self.recorder.as_mut() {
                    r.flush()?;
                }
                self.event(Event::new(self.t(), "pause", "Duraklatıldı / Paused"));
                let _ = self.tx.send(EngineEvent::Paused);
            }
            Command::End => self.end(),
            Command::ResetStats => {
                for s in self.stats.values_mut() {
                    *s = Stats::default();
                }
                self.violations = 0;
                self.shared
                    .lock()
                    .unwrap_or_else(|e| e.into_inner())
                    .serial_stats
                    .clear();
            }
            Command::Options(options) => {
                self.config.interval_ms = options.interval_ms.max(40);
                self.config.limit = options.limit;
                self.config.limit_series = options.key;
                self.config.low = options.low;
                self.config.high = options.high;
                if !self.config.limit {
                    self.alarm = None;
                }
            }
            Command::Mark(note) => self.event(Event::new(
                self.t(),
                "mark",
                format!("İşaret / Mark: {note}"),
            )),
            Command::Screenshot(path) => {
                if self.client.is_none() {
                    self.connect()?;
                }
                if self.dialect != Dialect::Siglent {
                    bail!("Scope screenshot is supported for Siglent only");
                }
                let data = self.client.as_mut().unwrap().query("SCDP")?;
                let mut path = path;
                if path.extension().is_none() {
                    path.set_extension("bmp");
                }
                std::fs::write(&path, data)?;
                let _ = self.tx.send(EngineEvent::Screenshot(path));
            }
            Command::Raw(command) => {
                if self.client.is_none() {
                    self.connect()?;
                }
                let response = if command.contains('?') {
                    self.client.as_mut().unwrap().text(&command)?
                } else {
                    self.client.as_mut().unwrap().write(&command)?;
                    "OK".into()
                };
                self.event(Event::new(
                    self.t(),
                    "scpi",
                    format!("{command} → {response}"),
                ));
            }
            Command::Shutdown => {
                self.end();
                return Ok(false);
            }
        }
        Ok(true)
    }
    fn sample(&mut self) -> Result<()> {
        if self.client.is_none() {
            self.connect()?;
        }
        let mut values = vec![];
        for ch in 0..4 {
            if !self.config.channels[ch] {
                continue;
            }
            let result = crate::protocol::measure(
                self.client.as_mut().unwrap().as_mut(),
                self.dialect,
                ch + 1,
                &self.config.params,
            )?;
            for (code, v) in self.config.params.iter().zip(result) {
                values.push((format!("C{}_{code}", ch + 1), v));
            }
        }
        let t = self.t();
        for (k, v) in &values {
            self.stats.entry(k.clone()).or_default().add(*v);
        }
        if let Some(r) = self.recorder.as_mut() {
            r.sample(t, &values.iter().map(|(_, v)| *v).collect::<Vec<_>>())?;
        }
        self.samples += 1;
        if self.config.limit
            && let Some((_, v)) = values.iter().find(|(k, _)| k == &self.config.limit_series)
        {
            let bad = !v.is_finite()
                || self.config.low.is_some_and(|l| *v < l)
                || self.config.high.is_some_and(|h| *v > h);
            if bad && self.alarm.is_none() {
                self.violations += 1;
                self.alarm = Some(Instant::now());
                self.alarm_t = t;
                let mut e = Event::new(
                    t,
                    "limit",
                    format!("Limit dışı / Out of limit: {}", self.config.limit_series),
                );
                e.value = v.to_string();
                self.event(e);
            } else if !bad && let Some(start) = self.alarm.take() {
                let mut e = Event::new(
                    self.alarm_t,
                    "recovery",
                    format!("Normale döndü / Recovered: {}", self.config.limit_series),
                );
                e.duration = start.elapsed().as_secs_f64();
                e.value = v.to_string();
                self.event(e);
            }
        }
        let _ = self.tx.send(EngineEvent::Sample { t, values });
        Ok(())
    }
    fn run(mut self) {
        'worker: loop {
            let wait = if self.running {
                self.next
                    .saturating_duration_since(Instant::now())
                    .min(Duration::from_millis(100))
            } else {
                Duration::from_millis(200)
            };
            match self.rx.recv_timeout(wait) {
                Ok(command) => match self.handle(command) {
                    Ok(false) => break,
                    Ok(true) => {}
                    Err(e) => {
                        let _ = self.tx.send(EngineEvent::Error(format!("{e:#}")));
                    }
                },
                Err(crossbeam_channel::RecvTimeoutError::Disconnected) => {
                    self.end();
                    break;
                }
                Err(_) => {}
            }
            // Honor stop/pause queued behind start before issuing another I/O.
            while let Ok(command) = self.rx.try_recv() {
                match self.handle(command) {
                    Ok(false) => break 'worker,
                    Ok(true) => {}
                    Err(e) => {
                        let _ = self.tx.send(EngineEvent::Error(format!("{e:#}")));
                    }
                }
            }
            if self.running && Instant::now() >= self.next {
                if let Err(e) = self.sample() {
                    self.client = None;
                    if !self.cancel.load(Ordering::Relaxed) {
                        self.retry += 1;
                        self.event(Event::new(
                            self.t(),
                            "error",
                            format!("İletişim hatası / Communication error: {e:#}"),
                        ));
                        let _ = self.tx.send(EngineEvent::State(
                            "Yeniden bağlanıyor / Reconnecting".into(),
                        ));
                        self.next = Instant::now()
                            + Duration::from_millis((self.retry as u64 * 800).min(5000));
                    } else {
                        self.next = Instant::now() + Duration::from_millis(100);
                    }
                } else {
                    self.next =
                        Instant::now() + Duration::from_millis(self.config.interval_ms.max(40));
                }
            }
        }
        let _ = self.tx.send(EngineEvent::Closed);
    }
}

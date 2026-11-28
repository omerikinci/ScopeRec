use super::*;
use crate::simulator::{SimConfig, SimServer};
use std::io::Write;

pub(super) struct Endurance {
    _server: SimServer,
    output: PathBuf,
    seconds: f64,
    start: Option<Instant>,
    deadline: Instant,
    stage: u8,
    period: i64,
    frames: u64,
    moves: u64,
    last_input: Instant,
    stem: Option<PathBuf>,
    actions: Vec<String>,
    error: Option<String>,
    progress: std::fs::File,
    graph_cleared: bool,
    serial_cleared: bool,
    capture_requested: bool,
    captured: bool,
}
impl Endurance {
    pub fn new(app: &mut ScopeApp, seconds: f64, output: PathBuf) -> anyhow::Result<Self> {
        std::fs::create_dir_all(&output)?;
        let server = SimServer::start("127.0.0.1:0", SimConfig::default())?;
        app.config = Config {
            connection: Connection::Lan,
            address: server.address.to_string(),
            record: true,
            directory: output.clone(),
            test_name: "Rust-SIM-endurance".into(),
            channels: [true, true, false, false],
            params: vec!["RMS".into(), "MEAN".into(), "PKPK".into()],
            interval_ms: 40,
            serial_side: true,
            sound: false,
            limit: true,
            limit_series: "C1_RMS".into(),
            high: Some(0.8),
            ..Default::default()
        };
        app.plot.window = 30.;
        app.plot.window_text = "30 s".into();
        for i in 0..3 {
            app.config.serial[i].record = true;
            app.connect_serial(i);
        }
        app.busy = true;
        app.engine.send(Command::Connect(app.config.clone()));
        Ok(Self {
            _server: server,
            output: output.clone(),
            seconds,
            start: None,
            deadline: Instant::now() + Duration::from_secs(15),
            stage: 0,
            period: -1,
            frames: 0,
            moves: 0,
            last_input: Instant::now(),
            stem: None,
            actions: vec![],
            error: None,
            progress: std::fs::File::create(output.join("progress.log"))?,
            graph_cleared: false,
            serial_cleared: false,
            capture_requested: false,
            captured: false,
        })
    }
    pub fn input(&mut self, raw: &mut egui::RawInput, rect: egui::Rect) {
        self.frames += 1;
        if self.stage != 1
            || !rect.is_positive()
            || self.last_input.elapsed() < Duration::from_millis(16)
        {
            return;
        }
        self.last_input = Instant::now();
        let f = self.frames as f32;
        let pos = egui::pos2(
            rect.left() + rect.width() * (0.5 + 0.4 * (f / 37.).sin()),
            rect.top() + rect.height() * (0.5 + 0.4 * (f / 51.).sin()),
        );
        raw.events.push(egui::Event::PointerMoved(pos));
        self.moves += 1;
        if self.frames.is_multiple_of(45) {
            raw.events.push(egui::Event::MouseWheel {
                unit: egui::MouseWheelUnit::Point,
                delta: Vec2::new(
                    0.,
                    if self.frames.is_multiple_of(90) {
                        8.
                    } else {
                        -8.
                    },
                ),
                modifiers: egui::Modifiers::default(),
            });
        }
    }
    fn say(&mut self, s: &str) {
        let _ = writeln!(self.progress, "{s}");
        let _ = self.progress.flush();
        println!("{s}");
    }
    pub fn tick(&mut self, app: &mut ScopeApp, ctx: &egui::Context) {
        if self.stage == 4 {
            return;
        }
        if let Err(e) = self.step(app, ctx) {
            self.error = Some(format!("{e:#}"));
            self.say(&format!("FAIL: {e:#}"));
            self.stage = 3;
            self.deadline = Instant::now() + Duration::from_secs(15);
            app.engine.send(Command::End);
            for session in app.serial.iter().flatten() {
                session.send(SerialCommand::Stop);
            }
        }
    }
    fn step(&mut self, app: &mut ScopeApp, ctx: &egui::Context) -> anyhow::Result<()> {
        let captures = ctx.input(|input| {
            input
                .events
                .iter()
                .filter_map(|event| {
                    if let egui::Event::Screenshot { image, .. } = event {
                        Some(image.clone())
                    } else {
                        None
                    }
                })
                .collect::<Vec<_>>()
        });
        for image in captures {
            let bytes = image.pixels.iter().flat_map(|p| p.to_array()).collect();
            image::RgbaImage::from_raw(image.width() as u32, image.height() as u32, bytes)
                .ok_or_else(|| anyhow::anyhow!("invalid screenshot dimensions"))?
                .save(self.output.join("interface.png"))?;
            self.captured = true;
        }
        if self.error.is_none() {
            anyhow::ensure!(app.error.is_none(), "UI error: {:?}", app.error);
        }
        let now = Instant::now();
        match self.stage {
            0 => {
                anyhow::ensure!(now < self.deadline, "connection timed out");
                if app.connected && !app.busy {
                    app.engine.send(Command::Start(app.config.clone()));
                    self.stage = 5;
                }
            }
            5 => {
                if app.running {
                    if self.start.is_none() {
                        self.start = Some(now);
                        self.stem = app.stem.clone();
                    }
                    anyhow::ensure!(self.stem.is_some(), "recorder missing");
                    self.stage = 1;
                    self.say(
                        "SIM connected: independent TCP SCPI server + 3 virtual serial sessions",
                    );
                }
            }
            1 => {
                let elapsed = self.start.unwrap().elapsed().as_secs_f64();
                anyhow::ensure!(app.running, "measurement stopped unexpectedly");
                if elapsed > 3. && !self.capture_requested {
                    ctx.send_viewport_cmd(egui::ViewportCommand::Screenshot(
                        egui::UserData::default(),
                    ));
                    self.capture_requested = true;
                }
                if elapsed >= self.seconds {
                    app.engine.send(Command::End);
                    self.stage = 3;
                    self.deadline = now + Duration::from_secs(15);
                    for session in app.serial.iter().flatten() {
                        session.send(SerialCommand::Stop);
                    }
                    return Ok(());
                }
                let period = (elapsed / 15.) as i64;
                if period != self.period {
                    self.period = period;
                    app.plot.all(elapsed);
                    app.plot.shift(-0.25);
                    app.plot.auto_y = true;
                    app.plot.follow = true;
                    app.config.serial_side = period % 2 == 0;
                    app.config.english = period % 8 >= 4;
                    app.config.high = Some(if period % 4 < 2 { 0.8 } else { 2.0 });
                    self.actions.push("navigate_resize_limits".into());
                    self.say(&format!("elapsed={elapsed:.1}s samples={} frames={} cursor_moves={} serial_lines={}",app.samples,self.frames,self.moves,app.serial_ui.iter().map(|s|s.logs.len()).sum::<usize>()));
                    if period > 0 && period % 4 == 0 {
                        app.engine.send(Command::Pause);
                        self.stage = 2;
                        self.deadline = now + Duration::from_secs(5);
                        self.actions.push("pause".into());
                    }
                }
                if elapsed > self.seconds * 0.35 && !self.graph_cleared {
                    app.clear_graph();
                    self.graph_cleared = true;
                    self.actions.push("clear_chart_while_recording".into());
                }
                if elapsed > self.seconds * 0.4 && !self.serial_cleared {
                    app.serial_ui[0].logs.clear();
                    self.serial_cleared = true;
                    self.actions.push("clear_terminal_while_recording".into());
                }
            }
            2 => {
                anyhow::ensure!(now < self.deadline, "pause timed out");
                if !app.running && !app.busy {
                    anyhow::ensure!(app.stem == self.stem, "recording changed across pause");
                    app.engine
                        .send(Command::Mark("Rust virtual test marker".into()));
                    app.engine.send(Command::Start(app.config.clone()));
                    self.stage = 5;
                    self.actions.push("resume".into());
                }
            }
            3 => {
                anyhow::ensure!(now < self.deadline, "end timed out");
                if app.test_active || app.running {
                    return Ok(());
                }
                if !app.serial.iter().flatten().all(SerialSession::finished) {
                    return Ok(());
                }
                self.finish(app, ctx)?;
            }
            _ => {}
        }
        Ok(())
    }
    fn finish(&mut self, app: &mut ScopeApp, ctx: &egui::Context) -> anyhow::Result<()> {
        let elapsed = self.start.map_or(0., |s| s.elapsed().as_secs_f64());
        if self.error.is_none() {
            anyhow::ensure!(self.captured, "native interface screenshot missing");
            let stem = self.stem.as_ref().unwrap();
            let mut recording = Recording::load(&recording::suffix(stem, ".csv"))?;
            let rows = recording.series["C1_RMS"].points.len();
            anyhow::ensure!(
                rows as u64 == app.samples,
                "CSV count {rows} != {}",
                app.samples
            );
            anyhow::ensure!(app.samples >= self.seconds as u64, "too few samples");
            anyhow::ensure!(
                recording.series.len() >= 15,
                "serial companion data missing: {}",
                recording.series.len()
            );
            anyhow::ensure!(
                app.events.iter().all(|e| e.kind != "error"),
                "communication error during test"
            );
            recording.analyze("C1_RMS", None, Some(0.9));
            anyhow::ensure!(recording.events.iter().any(|e| e.kind == "analysis"));
            recording::export_png(
                &self.output.join("test-chart.png"),
                &recording.series,
                None,
                true,
                Some("V"),
            )?;
            app.viewer_plot.all(recording.duration);
            app.viewer = Some(recording);
            app.tab = 2;
        }
        let report = serde_json::json!({"status":if self.error.is_none(){"PASS"}else{"FAIL"},"device":"virtual SCPI TCP server; no physical oscilloscope","seconds":elapsed,"samples":app.samples,"frames":self.frames,"cursor_moves":self.moves,"serial_sessions":3,"actions":self.actions,"error":self.error,"version":env!("CARGO_PKG_VERSION")});
        std::fs::write(
            self.output.join("result.json"),
            serde_json::to_vec_pretty(&report)?,
        )?;
        self.say(&report.to_string());
        self.stage = 4;
        app.closing = true;
        app.engine.send(Command::Shutdown);
        ctx.send_viewport_cmd(egui::ViewportCommand::Close);
        Ok(())
    }
}

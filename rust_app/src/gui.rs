use crate::{
    engine::{self, Command, Engine, EngineEvent, SharedRun},
    model::{Config, Connection, Dialect, Event, PARAMS, Series, format_value},
    platform,
    recording::{self, Recording},
    serial::{self, SerialCommand, SerialEvent, SerialSession},
};
use crossbeam_channel::{Receiver, Sender};
mod endurance;
use eframe::egui::{self, Color32, RichText, Vec2};
use egui_plot::{AxisHints, HLine, HPlacement, Line, Plot, Polygon, VLine};
use std::{
    collections::{BTreeMap, VecDeque},
    path::PathBuf,
    time::{Duration, Instant},
};
fn tr(en: bool, a: &'static str, b: &'static str) -> &'static str {
    if en { b } else { a }
}
fn c(rgb: [u8; 3]) -> Color32 {
    Color32::from_rgb(rgb[0], rgb[1], rgb[2])
}
fn action(ui: &mut egui::Ui, label: &str, color: [u8; 3], enabled: bool) -> bool {
    ui.add_enabled(
        enabled,
        egui::Button::new(RichText::new(label).color(Color32::from_rgb(234, 239, 245)))
            .fill(c(color))
            .min_size(Vec2::new(90., 30.)),
    )
    .clicked()
}
fn numeric(ui: &mut egui::Ui, label: &str, v: &mut Option<f64>) {
    ui.horizontal(|ui| {
        ui.label(label);
        let mut enabled = v.is_some();
        if ui.checkbox(&mut enabled, "").changed() {
            *v = if enabled { Some(0.) } else { None };
        }
        if let Some(n) = v {
            ui.add(egui::DragValue::new(n).speed(0.01).max_decimals(9));
        }
    });
}
#[derive(Clone)]
pub struct PlotState {
    pub follow: bool,
    pub window: f64,
    pub request: Option<(f64, f64)>,
    pub auto_y: bool,
    pub bounds: [f64; 4],
    pub unit: String,
    pub clock: bool,
    pub window_text: String,
    pub screen: egui::Rect,
}
impl Default for PlotState {
    fn default() -> Self {
        Self {
            follow: true,
            window: 120.,
            request: Some((0., 120.)),
            auto_y: true,
            bounds: [0., 120., -1., 1.],
            unit: "V".into(),
            clock: false,
            window_text: "120 s".into(),
            screen: egui::Rect::NOTHING,
        }
    }
}
impl PlotState {
    pub fn shift(&mut self, amount: f64) {
        let w = self.bounds[1] - self.bounds[0];
        let lo = (self.bounds[0] + w * amount).max(0.);
        self.request = Some((lo, lo + w));
        self.follow = false;
    }
    pub fn all(&mut self, last: f64) {
        self.follow = false;
        self.auto_y = true;
        self.request = Some((0., last.max(5.)));
    }
}

struct PlotOptions {
    en: bool,
    clock: Option<f64>,
    limits: Option<(Option<f64>, Option<f64>)>,
}
fn plot_view(
    ui: &mut egui::Ui,
    id: &str,
    series: &mut BTreeMap<String, Series>,
    events: &[Event],
    state: &mut PlotState,
    options: PlotOptions,
) {
    let PlotOptions { en, clock, limits } = options;
    ui.horizontal_wrapped(|ui| {
        for (label, amount) in [("◀◀", -1.), ("◀", -0.25), ("▶", 0.25), ("▶▶", 1.)] {
            if ui.button(label).clicked() {
                state.shift(amount);
            }
        }
        ui.checkbox(&mut state.follow, tr(en, "Canlı takip", "Follow live"));
        egui::Frame::new()
            .fill(Color32::from_rgb(37, 62, 72))
            .inner_margin(egui::Margin::symmetric(8, 5))
            .show(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.label(
                        RichText::new(tr(en, "Zaman aralığı", "Time window")).color(Color32::WHITE),
                    );
                    let changed = ui
                        .add(
                            egui::DragValue::new(&mut state.window)
                                .range(5.0..=864000.)
                                .suffix(" s")
                                .speed(5.),
                        )
                        .changed();
                    if changed {
                        state.window_text = format!("{} s", state.window);
                    }
                    let edit = ui
                        .add(egui::TextEdit::singleline(&mut state.window_text).desired_width(75.));
                    if (edit.lost_focus()
                        || edit.has_focus() && ui.input(|i| i.key_pressed(egui::Key::Enter)))
                        && let Some(value) = crate::model::parse_duration(&state.window_text)
                    {
                        state.window = value;
                    }
                });
            });
        if ui.button(tr(en, "Tüm zaman", "All time")).clicked() {
            let last = series
                .values()
                .filter_map(|s| s.points.back().map(|p| p[0]))
                .fold(5., f64::max);
            state.all(last);
        }
        if ui.button(tr(en, "Y otomatik", "Auto Y")).clicked() {
            state.auto_y = true;
        }
        let mut units: Vec<_> = series
            .values()
            .filter(|s| !s.serial)
            .map(|s| s.unit.clone())
            .collect();
        units.sort();
        units.dedup();
        if !units.is_empty() && !units.contains(&state.unit) {
            state.unit = units[0].clone();
        }
        egui::ComboBox::from_id_salt((id, "unit"))
            .selected_text(if state.unit.is_empty() {
                "SER"
            } else {
                &state.unit
            })
            .show_ui(ui, |ui| {
                for u in units {
                    ui.selectable_value(&mut state.unit, u.clone(), u);
                }
            });
        if clock.is_some() {
            ui.checkbox(&mut state.clock, tr(en, "Gerçek saat", "Clock time"));
        }
    });
    egui::ScrollArea::horizontal()
        .id_salt((id, "legend"))
        .max_height(50.)
        .show(ui, |ui| {
            ui.horizontal(|ui| {
                for s in series.values_mut() {
                    ui.checkbox(
                        &mut s.visible,
                        RichText::new(format!("● {} [{}]", s.key, s.unit)).color(c(s.color)),
                    )
                    .on_hover_text(tr(
                        en,
                        "Çizgiyi göster / gizle",
                        "Show / hide curve",
                    ));
                }
            });
        });
    let last = series
        .values()
        .filter_map(|s| s.points.back().map(|p| p[0]))
        .fold(0., f64::max);
    let view_width = (state.bounds[1] - state.bounds[0]).max(0.001);
    let max_start = (last - view_width).max(0.);
    let mut view_start = state.bounds[0].clamp(0., max_start);
    ui.horizontal(|ui| {
        ui.spacing_mut().slider_width = (ui.available_width() - 180.).max(80.);
        if ui
            .add_enabled(
                max_start > 0.,
                egui::Slider::new(&mut view_start, 0.0..=max_start.max(0.001)).text(tr(
                    en,
                    "Geçmişte gezin",
                    "Browse history",
                )),
            )
            .changed()
        {
            state.follow = false;
            state.request = Some((view_start, view_start + view_width));
        }
    });
    let requested = if state.follow {
        Some(((last - state.window).max(0.), last.max(state.window)))
    } else {
        state.request.take()
    };
    let (lo, hi) = requested.unwrap_or((state.bounds[0], state.bounds[1]));
    let mut yl = f64::INFINITY;
    let mut yh = f64::NEG_INFINITY;
    let mut rl = f64::INFINITY;
    let mut rh = f64::NEG_INFINITY;
    for s in series
        .values()
        .filter(|s| s.visible && (s.serial || s.unit == state.unit))
    {
        for p in s.envelope(lo, hi, 800) {
            if p[1].is_finite() {
                if s.serial {
                    rl = rl.min(p[1]);
                    rh = rh.max(p[1]);
                } else {
                    yl = yl.min(p[1]);
                    yh = yh.max(p[1]);
                }
            }
        }
    }
    if !yl.is_finite() {
        yl = -1.;
        yh = 1.;
    }
    let pad = ((yh - yl) * 0.08).max(yh.abs() * 0.01).max(1e-12);
    let (ylo, yhi) = if state.auto_y {
        (yl - pad, yh + pad)
    } else {
        (state.bounds[2], state.bounds[3])
    };
    if !rl.is_finite() {
        rl = 0.;
        rh = 1.;
    }
    if (rh - rl).abs() < 1e-12 {
        rl -= 1.;
        rh += 1.;
    }
    let serial_scale = (yhi - ylo) / (rh - rl);
    let mut plot = Plot::new(id)
        .height(ui.available_height().max(180.))
        .allow_zoom(false)
        .allow_scroll(false)
        .allow_drag(true)
        .allow_boxed_zoom(true)
        .allow_double_click_reset(false)
        .show_grid(true)
        .label_formatter(|_, _| String::new())
        .y_axis_label(&state.unit)
        .x_axis_label(tr(en, "Süre [s]", "Time [s]"));
    let has_serial = series.values().any(|s| s.serial && s.visible);
    if has_serial {
        plot = plot.custom_y_axes(vec![
            AxisHints::new_y().label(state.unit.clone()),
            AxisHints::new_y()
                .placement(HPlacement::Right)
                .label("Serial")
                .formatter(move |mark, _| format_value(rl + (mark.value - ylo) / serial_scale, "")),
        ]);
    }
    if state.clock
        && let Some(clock) = clock
    {
        plot = plot.x_axis_formatter(move |m, _| {
            chrono::DateTime::from_timestamp_millis(((clock + m.value) * 1000.) as i64)
                .map(|t| {
                    t.with_timezone(&chrono::Local)
                        .format("%H:%M:%S")
                        .to_string()
                })
                .unwrap_or_default()
        });
    }
    let (scroll, zoom, ctrl) = ui.input(|i| {
        (
            i.smooth_scroll_delta,
            i.zoom_delta(),
            i.modifiers.ctrl || i.modifiers.command,
        )
    });
    let mut hover = None;
    let mut interacting = false;
    let available_width = ui.available_width().max(1.);
    let response = plot.show(ui, |p| {
        p.set_auto_bounds([false, false]);
        if let Some((a, b)) = requested {
            p.set_plot_bounds_x(a..=b);
        }
        if state.auto_y {
            p.set_plot_bounds_y(ylo..=yhi);
        }
        if p.response().contains_pointer() {
            if scroll.y.abs() > 0.01 || (zoom - 1.).abs() > 0.001 {
                let factor = if (zoom - 1.).abs() > 0.001 {
                    zoom
                } else {
                    (scroll.y * 0.006).exp().clamp(0.2, 5.)
                };
                p.zoom_bounds_around_hovered(if ctrl {
                    Vec2::new(1., factor)
                } else {
                    Vec2::new(factor, 1.)
                });
                interacting = true;
                if ctrl {
                    state.auto_y = false;
                }
            }
            if scroll.x.abs() > 0.01 {
                let w = p.plot_bounds().width();
                p.translate_bounds(Vec2::new(-scroll.x * w as f32 / available_width, 0.));
                interacting = true;
            }
        }
        let bounds = p.plot_bounds();
        let bmin = bounds.min();
        let bmax = bounds.max();
        for event in events
            .iter()
            .filter(|e| e.t <= bmax[0] && e.t + e.duration >= bmin[0])
        {
            if event.kind == "mark" {
                p.vline(VLine::new(&event.text, event.t).color(Color32::from_rgb(109, 155, 208)));
            } else if event.duration > 0.
                && ["limit", "recovery", "analysis", "gap"].contains(&event.kind.as_str())
            {
                p.polygon(
                    Polygon::new(
                        &event.text,
                        vec![
                            [event.t, bmin[1]],
                            [event.t + event.duration, bmin[1]],
                            [event.t + event.duration, bmax[1]],
                            [event.t, bmax[1]],
                        ],
                    )
                    .fill_color(Color32::from_rgba_unmultiplied(190, 65, 76, 30))
                    .stroke(egui::Stroke::NONE),
                );
            }
        }
        if let Some((low, high)) = limits {
            for v in [low, high].into_iter().flatten() {
                p.hline(
                    HLine::new("Limit", v)
                        .color(Color32::from_rgb(193, 92, 98))
                        .style(egui_plot::LineStyle::dashed_loose()),
                );
            }
        }
        let pointer = p.pointer_coordinate();
        let mut best = f64::INFINITY;
        for s in series
            .values()
            .filter(|s| s.visible && (s.serial || s.unit == state.unit))
        {
            let points = s.envelope(bmin[0], bmax[0], p.response().rect.width() as usize);
            let mut run = vec![];
            for pt in points.into_iter().chain(std::iter::once([0., f64::NAN])) {
                if !pt[1].is_finite() {
                    if !run.is_empty() {
                        p.line(
                            Line::new(&s.key, std::mem::take(&mut run))
                                .color(c(s.color))
                                .width(1.6_f32),
                        );
                    }
                    continue;
                }
                run.push([
                    pt[0],
                    if s.serial {
                        ylo + (pt[1] - rl) * serial_scale
                    } else {
                        pt[1]
                    },
                ]);
            }
            if let Some(pointer) = pointer
                && let Some(point) = s.nearest(pointer.x)
                && point[1].is_finite()
                && (point[0] - pointer.x).abs() < (bounds.width() / 100.).max(1.)
            {
                let raw_y = if s.serial {
                    ylo + (point[1] - rl) * serial_scale
                } else {
                    point[1]
                };
                let d = (raw_y - pointer.y).abs();
                if d < best {
                    best = d;
                    hover = Some((s.key.clone(), s.unit.clone(), point, s.color));
                }
            }
        }
        if p.response().dragged() {
            interacting = true;
            state.auto_y = false;
        }
    });
    let bounds = response.transform.bounds();
    state.screen = response.response.rect;
    let min = bounds.min();
    let max = bounds.max();
    state.bounds = [min[0], max[0], min[1], max[1]];
    if interacting {
        state.follow = false;
    }
    if response.response.double_clicked() {
        state.all(last);
    }
    if response.response.hovered()
        && !response.response.dragged()
        && let (Some((key, unit, point, color)), Some(pos)) = (hover, ui.ctx().pointer_hover_pos())
    {
        let text = format!(
            "{key}\n{}: {:.3} s\n{}: {}",
            tr(en, "Zaman", "Time"),
            point[0],
            tr(en, "Değer", "Value"),
            format_value(point[1], &unit)
        );
        egui::Area::new(egui::Id::new((id, "cursor_info")))
            .order(egui::Order::Tooltip)
            .pivot(egui::Align2::LEFT_BOTTOM)
            .fixed_pos(pos + Vec2::new(14., -12.))
            .interactable(false)
            .show(ui.ctx(), |ui| {
                egui::Frame::popup(ui.style())
                    .stroke(egui::Stroke::new(1_f32, c(color)))
                    .show(ui, |ui| {
                        ui.label(text);
                    });
            });
    }
}

#[derive(Default)]
struct SerialUi {
    logs: VecDeque<String>,
    values: BTreeMap<String, crate::model::Stats>,
    send: String,
    state: String,
    connected: bool,
    autoscroll: bool,
}
pub struct ScopeApp {
    pub config: Config,
    engine: Engine,
    shared: SharedRun,
    series: BTreeMap<String, Series>,
    events: VecDeque<Event>,
    logs: VecDeque<String>,
    connected: bool,
    running: bool,
    test_active: bool,
    busy: bool,
    status: String,
    device: String,
    samples: u64,
    violations: u64,
    stem: Option<PathBuf>,
    tab: usize,
    plot: PlotState,
    viewer_plot: PlotState,
    viewer: Option<Recording>,
    viewer_rx: Option<Receiver<anyhow::Result<Recording>>>,
    serial: [Option<SerialSession>; 3],
    retired_serial: Vec<(usize, SerialSession)>,
    serial_ui: [SerialUi; 3],
    serial_tx: Sender<SerialEvent>,
    serial_rx: Receiver<SerialEvent>,
    serial_active: usize,
    ports: Vec<String>,
    usb: Vec<crate::transport::UsbDevice>,
    usb_rx: Option<Receiver<anyhow::Result<Vec<crate::transport::UsbDevice>>>>,
    error: Option<String>,
    mark_note: String,
    scpi: String,
    analysis_key: String,
    analysis_low: Option<f64>,
    analysis_high: Option<f64>,
    event_filter: String,
    last_save: Instant,
    live_options: Option<engine::LiveOptions>,
    saved: String,
    sleep: platform::SleepGuard,
    close_prompt: bool,
    closing: bool,
    logo: egui::TextureHandle,
    isolated: bool,
    endurance: Option<endurance::Endurance>,
}
impl ScopeApp {
    pub fn new(cc: &eframe::CreationContext<'_>, demo: bool) -> Self {
        let mut config = Config::load();
        if demo {
            config = Config {
                dark: config.dark,
                english: config.english,
                ..Default::default()
            };
        }
        let shared = engine::shared_run();
        let engine = Engine::new(shared.clone());
        let (serial_tx, serial_rx) = serial::channel();
        let image = image::load_from_memory(include_bytes!("../../logo/scoperec-kare.png"))
            .unwrap()
            .to_rgba8();
        let logo = cc.egui_ctx.load_texture(
            "ScopeRec Rust",
            egui::ColorImage::from_rgba_unmultiplied(
                [image.width() as usize, image.height() as usize],
                image.as_raw(),
            ),
            Default::default(),
        );
        let mut app = Self {
            plot: PlotState {
                window: config.window,
                window_text: format!("{} s", config.window),
                unit: config.selected_unit.clone(),
                ..Default::default()
            },
            viewer_plot: PlotState {
                follow: false,
                ..Default::default()
            },
            config,
            engine,
            shared,
            series: BTreeMap::new(),
            events: VecDeque::new(),
            logs: VecDeque::new(),
            connected: false,
            running: false,
            test_active: false,
            busy: false,
            status: "Hazır / Ready".into(),
            device: String::new(),
            samples: 0,
            violations: 0,
            stem: None,
            tab: 0,
            viewer: None,
            viewer_rx: None,
            serial: std::array::from_fn(|_| None),
            retired_serial: vec![],
            serial_ui: std::array::from_fn(|_| SerialUi {
                autoscroll: true,
                ..Default::default()
            }),
            serial_tx,
            serial_rx,
            serial_active: 0,
            ports: serial::ports(),
            usb: vec![],
            usb_rx: None,
            error: None,
            mark_note: String::new(),
            scpi: String::new(),
            analysis_key: String::new(),
            analysis_low: None,
            analysis_high: None,
            event_filter: String::new(),
            last_save: Instant::now(),
            live_options: None,
            saved: String::new(),
            sleep: Default::default(),
            close_prompt: false,
            closing: false,
            logo,
            isolated: demo,
            endurance: None,
        };
        app.theme(&cc.egui_ctx);
        for i in 0..3 {
            if app.config.serial[i].auto {
                app.connect_serial(i);
            }
        }
        app
    }
    pub fn enable_self_test(&mut self, seconds: f64, output: PathBuf) -> anyhow::Result<()> {
        self.isolated = true;
        self.endurance = Some(endurance::Endurance::new(self, seconds, output)?);
        Ok(())
    }
    fn clear_graph(&mut self) {
        for s in self.series.values_mut() {
            s.clear();
        }
        self.events.retain(|e| e.kind != "mark");
        self.plot.follow = true;
    }
    fn theme(&self, ctx: &egui::Context) {
        let mut v = if self.config.dark {
            egui::Visuals::dark()
        } else {
            egui::Visuals::light()
        };
        if self.config.dark {
            v.panel_fill = Color32::from_rgb(17, 25, 37);
            v.window_fill = v.panel_fill;
            v.extreme_bg_color = Color32::from_rgb(10, 18, 29);
            v.faint_bg_color = Color32::from_rgb(24, 35, 49);
            v.widgets.noninteractive.bg_fill = Color32::from_rgb(24, 35, 49);
            v.widgets.inactive.bg_fill = Color32::from_rgb(35, 47, 62);
        }
        ctx.set_visuals(v);
        let mut style = (*ctx.style()).clone();
        style.spacing.item_spacing = Vec2::new(8., 7.);
        style.spacing.interact_size.y = 27.;
        style.interaction.resize_grab_radius_side = 10.;
        style.interaction.resize_grab_radius_corner = 16.;
        ctx.set_style(style);
    }
    fn log(&mut self, event: Event) {
        if event.kind == "error" {
            self.connected = false;
            for s in self.series.values_mut().filter(|s| !s.serial) {
                if s.points.back().is_some_and(|p| p[0] <= event.t) {
                    s.add(event.t, f64::NAN, self.config.retention);
                }
            }
        }
        if event.kind == "limit" {
            self.violations += 1;
            if self.config.sound {
                platform::beep();
            }
        }
        self.logs.push_back(format!(
            "{}  {}",
            chrono::Local::now().format("%H:%M:%S"),
            event.text
        ));
        self.events.push_back(event);
        while self.logs.len() > 5000 {
            self.logs.pop_front();
        }
        while self.events.len() > 5000 {
            self.events.pop_front();
        }
    }
    fn connect_serial(&mut self, i: usize) {
        self.serial_ui[i].state = tr(self.config.english, "Bağlanıyor…", "Connecting…").into();
        self.serial[i] = Some(SerialSession::start(
            i,
            self.config.serial[i].clone(),
            self.shared.clone(),
            self.config.directory.clone(),
            self.serial_tx.clone(),
        ));
    }
    fn poll(&mut self) {
        for event in self
            .engine
            .events
            .try_iter()
            .take(10000)
            .collect::<Vec<_>>()
        {
            match event {
                EngineEvent::Connected(id, _) => {
                    self.device = id;
                    self.connected = true;
                    self.busy = false;
                    self.status = tr(self.config.english, "Bağlandı", "Connected").into();
                }
                EngineEvent::State(s) => self.status = s,
                EngineEvent::Started {
                    keys,
                    stem,
                    resumed,
                    t,
                } => {
                    if !resumed {
                        self.series.clear();
                        for ui in &mut self.serial_ui {
                            for stats in ui.values.values_mut() {
                                *stats = Default::default();
                            }
                        }
                        self.events.clear();
                        self.samples = 0;
                        self.violations = 0;
                        for key in keys {
                            self.series.insert(key.clone(), Series::new(&key, false));
                        }
                    } else {
                        for s in self.series.values_mut() {
                            s.add(t, f64::NAN, self.config.retention);
                        }
                    }
                    self.stem = stem;
                    self.running = true;
                    self.test_active = true;
                    self.busy = false;
                    self.plot.follow = true;
                }
                EngineEvent::Sample { t, values } => {
                    self.samples += 1;
                    for (key, v) in values {
                        if let Some(s) = self.series.get_mut(&key) {
                            s.add(t, v, self.config.retention);
                        }
                    }
                }
                EngineEvent::Log(e) => self.log(e),
                EngineEvent::Paused => {
                    self.running = false;
                    self.connected = false;
                    self.busy = false;
                    self.status = tr(self.config.english, "Duraklatıldı", "Paused").into();
                }
                EngineEvent::Ended {
                    samples,
                    violations,
                } => {
                    self.samples = samples;
                    self.violations = violations;
                    self.running = false;
                    self.test_active = false;
                    self.connected = false;
                    self.busy = false;
                    self.status =
                        tr(self.config.english, "Test tamamlandı", "Test complete").into();
                }
                EngineEvent::Screenshot(p) => {
                    self.status = p.display().to_string();
                }
                EngineEvent::Error(e) => {
                    self.busy = false;
                    self.error = Some(e.clone());
                    self.log(Event::new(0., "error", e));
                }
                EngineEvent::Closed => {
                    self.running = false;
                    self.connected = false;
                    self.busy = false;
                }
            }
        }
        for event in self.serial_rx.try_iter().take(10000).collect::<Vec<_>>() {
            match event {
                SerialEvent::Connected(i, p) => {
                    self.serial_ui[i].connected = true;
                    self.serial_ui[i].state = p;
                }
                SerialEvent::Line(i, t, line) => {
                    let line = if self.config.serial[i].stamp {
                        format!(
                            "{}  {t:.3}  {line}",
                            chrono::Local::now().format("%H:%M:%S")
                        )
                    } else {
                        line
                    };
                    self.serial_ui[i].logs.push_back(line);
                    while self.serial_ui[i].logs.len() > 5000 {
                        self.serial_ui[i].logs.pop_front();
                    }
                }
                SerialEvent::Values(i, generation, t, values) => {
                    if generation
                        != self
                            .shared
                            .lock()
                            .unwrap_or_else(|e| e.into_inner())
                            .generation
                    {
                        continue;
                    }
                    for (key, v) in values {
                        if self.serial_ui[i].values.len() >= 32
                            && !self.serial_ui[i].values.contains_key(&key)
                        {
                            continue;
                        }
                        self.serial_ui[i]
                            .values
                            .entry(key.clone())
                            .or_default()
                            .add(v);
                        let full = format!("Serial{}_{}", i + 1, key);
                        let s = self
                            .series
                            .entry(full.clone())
                            .or_insert_with(|| Series::new(&full, true));
                        if self.config.serial[i].plot {
                            s.add(t, v, self.config.retention);
                        }
                    }
                }
                SerialEvent::State(i, s) | SerialEvent::Error(i, s) => {
                    self.serial_ui[i].state = s;
                    self.serial_ui[i].connected = false;
                }
                SerialEvent::Closed(i) => {
                    self.serial_ui[i].connected = false;
                    self.serial_ui[i].state =
                        tr(self.config.english, "Bağlı değil", "Disconnected").into();
                }
            }
        }
        self.retired_serial
            .retain(|(_, session)| !session.finished());
        if let Some(rx) = self.viewer_rx.as_ref()
            && let Ok(result) = rx.try_recv()
        {
            self.viewer_rx = None;
            match result {
                Ok(r) => {
                    self.analysis_key = r.series.keys().next().cloned().unwrap_or_default();
                    self.viewer_plot.all(r.duration);
                    self.viewer = Some(r);
                    self.tab = 2;
                }
                Err(e) => self.error = Some(format!("{e:#}")),
            }
        }
        if let Some(rx) = self.usb_rx.as_ref()
            && let Ok(result) = rx.try_recv()
        {
            self.usb_rx = None;
            match result {
                Ok(devices) => {
                    if self.config.usb.is_empty()
                        && let Some(d) = devices.first()
                    {
                        self.config.usb = d.selector.clone();
                    }
                    self.usb = devices;
                }
                Err(e) => self.error = Some(e.to_string()),
            }
        }
    }
    fn open_files(&mut self, files: Vec<PathBuf>) {
        if files.is_empty() {
            return;
        }
        let (tx, rx) = crossbeam_channel::bounded(1);
        self.viewer_rx = Some(rx);
        self.tab = 2;
        std::thread::spawn(move || {
            let result = (|| {
                let first = files
                    .iter()
                    .find(|p| {
                        p.extension().is_some_and(|s| s == "csv")
                            && !p.to_string_lossy().contains("_olaylar")
                    })
                    .unwrap_or(&files[0]);
                let mut r = Recording::load(first)?;
                for path in files.iter().filter(|p| *p != first) {
                    // CSV loading already includes these files. Adding them twice
                    // would duplicate points and break the sorted time history.
                    let stem = r.path.with_extension("");
                    if [
                        "_seri.txt",
                        "_seri2.txt",
                        "_seri3.txt",
                        "_log.txt",
                        "_olaylar.csv",
                    ]
                    .iter()
                    .any(|tag| recording::suffix(&stem, tag) == *path)
                    {
                        continue;
                    }
                    if path.to_string_lossy().contains("seri") {
                        r.add_serial(path)?;
                    } else {
                        r.add_events(path)?;
                    }
                }
                Ok(r)
            })();
            let _ = tx.send(result);
        });
    }
    fn export(&mut self, viewer: bool) {
        if let Some(path) = rfd::FileDialog::new()
            .add_filter("PNG", &["png"])
            .set_file_name("ScopeRec-Rust.png")
            .save_file()
        {
            let result = if viewer {
                self.viewer
                    .as_ref()
                    .map(|r| {
                        recording::export_png(
                            &path,
                            &r.series,
                            Some((self.viewer_plot.bounds[0], self.viewer_plot.bounds[1])),
                            self.config.dark,
                            Some(&self.viewer_plot.unit),
                        )
                    })
                    .unwrap_or(Ok(()))
            } else {
                recording::export_png(
                    &path,
                    &self.series,
                    Some((self.plot.bounds[0], self.plot.bounds[1])),
                    self.config.dark,
                    Some(&self.plot.unit),
                )
            };
            if let Err(e) = result {
                self.error = Some(e.to_string());
            } else {
                self.status = path.display().to_string();
            }
        }
    }
    fn top(&mut self, ctx: &egui::Context) {
        let en = self.config.english;
        egui::TopBottomPanel::top("connection").show(ctx, |ui| {
            ui.horizontal_wrapped(|ui| {
                ui.image((self.logo.id(), Vec2::splat(30.)));
                ui.heading("ScopeRec Rust");
                ui.separator();
                ui.add_enabled_ui(
                    !self.running && !self.busy && !self.connected && !self.test_active,
                    |ui| {
                        ui.selectable_value(
                            &mut self.config.connection,
                            Connection::Simulator,
                            tr(en, "Sanal cihaz", "Simulator"),
                        );
                        ui.selectable_value(&mut self.config.connection, Connection::Lan, "LAN");
                        ui.selectable_value(&mut self.config.connection, Connection::Usb, "USB");
                        match self.config.connection {
                            Connection::Lan => {
                                ui.add(
                                    egui::TextEdit::singleline(&mut self.config.address)
                                        .desired_width(220.)
                                        .hint_text("192.168.1.50:5025"),
                                );
                            }
                            Connection::Usb => {
                                egui::ComboBox::from_id_salt("usb")
                                    .width(220.)
                                    .selected_text(if self.config.usb.is_empty() {
                                        tr(en, "USB cihaz seçin", "Select USB device")
                                    } else {
                                        &self.config.usb
                                    })
                                    .show_ui(ui, |ui| {
                                        for d in &self.usb {
                                            ui.selectable_value(
                                                &mut self.config.usb,
                                                d.selector.clone(),
                                                &d.label,
                                            );
                                        }
                                    });
                                if ui.button(tr(en, "USB tara", "Scan USB")).clicked() {
                                    let (tx, rx) = crossbeam_channel::bounded(1);
                                    self.usb_rx = Some(rx);
                                    std::thread::spawn(move || {
                                        let _ = tx.send(crate::transport::usb_devices());
                                    });
                                }
                            }
                            Connection::Simulator => {
                                ui.label(
                                    RichText::new(tr(
                                        en,
                                        "Fiziksel cihaz kullanılmaz",
                                        "No physical device used",
                                    ))
                                    .color(Color32::from_rgb(92, 176, 146)),
                                );
                            }
                        }
                    },
                );
                if action(
                    ui,
                    tr(
                        en,
                        if self.connected || self.running {
                            "Bağlantıyı kes"
                        } else {
                            "Osiloskopa bağlan"
                        },
                        if self.connected || self.running {
                            "Disconnect"
                        } else {
                            "Connect scope"
                        },
                    ),
                    [49, 73, 92],
                    !self.busy && !self.closing,
                ) {
                    self.busy = true;
                    if self.connected || self.running {
                        self.engine.send(Command::Disconnect);
                    } else {
                        self.engine.send(Command::Connect(self.config.clone()));
                    }
                }
            });
            ui.horizontal_wrapped(|ui| {
                ui.label(tr(en, "Komut seti", "Dialect"));
                egui::ComboBox::from_id_salt("dialect")
                    .selected_text(self.config.dialect.label())
                    .show_ui(ui, |ui| {
                        for d in Dialect::ALL {
                            ui.selectable_value(&mut self.config.dialect, d, d.label());
                        }
                    });
                ui.label(tr(en, "Aralık", "Interval"));
                ui.add(
                    egui::DragValue::new(&mut self.config.interval_ms)
                        .range(40..=600000)
                        .suffix(" ms"),
                );
                if ui
                    .checkbox(&mut self.config.dark, tr(en, "Koyu tema", "Dark theme"))
                    .changed()
                {
                    self.theme(ctx);
                }
                ui.checkbox(&mut self.config.english, "English").changed();
                ui.checkbox(
                    &mut self.config.serial_side,
                    tr(en, "Seri sağ panel", "Serial side panel"),
                );
                if !self.device.is_empty() {
                    ui.label(RichText::new(&self.device).small());
                }
            });
        });
    }
    fn quick_actions(&mut self, ctx: &egui::Context) {
        let en = self.config.english;
        egui::TopBottomPanel::top("primary_actions").show(ctx, |ui| {
            ui.horizontal_wrapped(|ui| {
                if action(
                    ui,
                    tr(
                        en,
                        if self.test_active {
                            "Devam et"
                        } else {
                            "Başlat"
                        },
                        if self.test_active { "Resume" } else { "Start" },
                    ),
                    [40, 77, 62],
                    !self.running && !self.busy && !self.closing,
                ) {
                    self.busy = true;
                    self.engine.send(Command::Start(self.config.clone()));
                }
                if action(
                    ui,
                    tr(en, "Duraklat", "Pause"),
                    [99, 81, 46],
                    self.running && !self.busy,
                ) {
                    self.busy = true;
                    self.engine.send(Command::Pause);
                }
                if action(
                    ui,
                    tr(en, "Testi bitir", "End test"),
                    [99, 52, 62],
                    self.test_active && !self.busy,
                ) {
                    self.busy = true;
                    self.engine.send(Command::End);
                }
                if ui.button(tr(en, "İşaret koy", "Add mark")).clicked() {
                    self.engine.send(Command::Mark(self.mark_note.clone()));
                    self.mark_note.clear();
                }
                if ui
                    .button(tr(en, "Grafiği temizle", "Clear chart"))
                    .clicked()
                {
                    for s in self.series.values_mut() {
                        s.clear();
                    }
                    self.events.retain(|e| e.kind != "mark");
                    self.plot.follow = true;
                }
                ui.add_enabled_ui(!self.test_active, |ui| {
                    ui.checkbox(
                        &mut self.config.record,
                        tr(en, "CSV kaydı", "CSV recording"),
                    );
                });
            });
        });
    }
    fn left(&mut self, ctx: &egui::Context) {
        let en = self.config.english;
        egui::SidePanel::left("settings")
            .default_width(270.)
            .min_width(240.)
            .max_width(440.)
            .resizable(true)
            .show(ctx, |ui| {
                egui::ScrollArea::vertical().show(ui, |ui| {
                    ui.heading(tr(en, "Kanallar", "Channels"));
                    ui.add_enabled_ui(!self.test_active, |ui| {
                        ui.horizontal(|ui| {
                            for (i, channel) in self.config.channels.iter_mut().enumerate() {
                                ui.checkbox(channel, format!("C{}", i + 1));
                            }
                        });
                        ui.separator();
                        ui.strong(tr(en, "Ölçümler", "Measurements"));
                        egui::ScrollArea::vertical()
                            .id_salt("parameter_selection")
                            .max_height(250.)
                            .show(ui, |ui| {
                                for p in PARAMS {
                                    let mut selected =
                                        self.config.params.iter().any(|x| x == p.code);
                                    if ui
                                        .checkbox(
                                            &mut selected,
                                            format!(
                                                "{} · {} [{}]",
                                                p.code,
                                                if en { p.en } else { p.tr },
                                                p.unit
                                            ),
                                        )
                                        .changed()
                                    {
                                        if selected {
                                            self.config.params.push(p.code.into());
                                        } else {
                                            self.config.params.retain(|x| x != p.code);
                                        }
                                    }
                                }
                            });
                        ui.separator();
                        ui.strong(tr(en, "Kayıt", "Recording"));
                        ui.label(tr(en, "Test adı", "Test name"));
                        ui.text_edit_singleline(&mut self.config.test_name);
                        ui.checkbox(
                            &mut self.config.record,
                            tr(en, "CSV / log / olay kaydet", "Record CSV / log / events"),
                        );
                        ui.label(self.config.directory.display().to_string());
                        if ui
                            .button(tr(en, "Kayıt klasörü…", "Record folder…"))
                            .clicked()
                            && let Some(p) = rfd::FileDialog::new()
                                .set_directory(&self.config.directory)
                                .pick_folder()
                        {
                            self.config.directory = p;
                        }
                    });
                    ui.separator();
                    ui.checkbox(
                        &mut self.config.limit,
                        tr(en, "Limit kontrolü", "Limit checking"),
                    );
                    let keys = self.config.keys();
                    egui::ComboBox::from_id_salt("limit_key")
                        .selected_text(&self.config.limit_series)
                        .show_ui(ui, |ui| {
                            for key in keys {
                                ui.selectable_value(
                                    &mut self.config.limit_series,
                                    key.clone(),
                                    key,
                                );
                            }
                        });
                    numeric(ui, tr(en, "Alt limit", "Low limit"), &mut self.config.low);
                    numeric(ui, tr(en, "Üst limit", "High limit"), &mut self.config.high);
                    ui.checkbox(&mut self.config.sound, tr(en, "Sesli uyarı", "Sound alert"));
                    ui.separator();
                    if action(
                        ui,
                        tr(
                            en,
                            if self.test_active {
                                "Devam et"
                            } else {
                                "Başlat"
                            },
                            if self.test_active { "Resume" } else { "Start" },
                        ),
                        [40, 77, 62],
                        !self.running && !self.busy,
                    ) {
                        self.busy = true;
                        self.engine.send(Command::Start(self.config.clone()));
                    }
                    ui.horizontal(|ui| {
                        if action(
                            ui,
                            tr(en, "Duraklat", "Pause"),
                            [99, 81, 46],
                            self.running && !self.busy,
                        ) {
                            self.busy = true;
                            self.engine.send(Command::Pause);
                        }
                        if action(
                            ui,
                            tr(en, "Testi bitir", "End test"),
                            [99, 52, 62],
                            self.test_active && !self.busy,
                        ) {
                            self.busy = true;
                            self.engine.send(Command::End);
                        }
                    });
                    if ui
                        .button(tr(en, "İstatistiği sıfırla", "Reset statistics"))
                        .clicked()
                    {
                        for s in self.series.values_mut() {
                            s.stats = Default::default();
                        }
                        for ui in &mut self.serial_ui {
                            for stats in ui.values.values_mut() {
                                *stats = Default::default();
                            }
                        }
                        self.violations = 0;
                        self.engine.send(Command::ResetStats);
                    }
                    ui.text_edit_singleline(&mut self.mark_note);
                    if ui.button(tr(en, "İşaret koy", "Add mark")).clicked() {
                        self.engine.send(Command::Mark(self.mark_note.clone()));
                        self.mark_note.clear();
                    }
                    if ui
                        .button(tr(en, "Osiloskop ekranı…", "Scope screenshot…"))
                        .clicked()
                        && let Some(p) = rfd::FileDialog::new()
                            .add_filter("BMP", &["bmp"])
                            .set_file_name("oscilloscope.bmp")
                            .save_file()
                    {
                        self.engine.send(Command::Screenshot(p));
                    }
                    if ui.button(tr(en, "Grafik PNG…", "Chart PNG…")).clicked() {
                        self.export(false);
                    }
                    if ui
                        .button(tr(en, "Son kaydı aç", "Open last recording"))
                        .clicked()
                        && let Some(stem) = &self.stem
                    {
                        self.open_files(vec![recording::suffix(stem, ".csv")]);
                    }
                    ui.collapsing(tr(en, "SCPI terminali", "SCPI terminal"), |ui| {
                        ui.text_edit_singleline(&mut self.scpi);
                        if ui.button(tr(en, "Gönder", "Send")).clicked() {
                            self.engine.send(Command::Raw(self.scpi.clone()));
                        }
                    });
                    ui.collapsing(tr(en, "Gelişmiş", "Advanced"), |ui| {
                        ui.label(tr(
                            en,
                            "Bellekteki nokta / seri",
                            "Retained points / series",
                        ));
                        ui.add(
                            egui::DragValue::new(&mut self.config.retention)
                                .range(1000..=7_000_000)
                                .speed(10000.),
                        );
                        if ui
                            .add_enabled(
                                !self.test_active,
                                egui::Button::new(tr(en, "Varsayılan ayarlar", "Restore defaults")),
                            )
                            .clicked()
                        {
                            self.config = Config::default();
                            self.theme(ctx);
                        }
                    });
                });
            });
    }
    fn serial_panel(&mut self, ui: &mut egui::Ui) {
        let en = self.config.english;
        ui.heading(tr(en, "Seri port", "Serial port"));
        ui.horizontal(|ui| {
            for i in 0..3 {
                ui.selectable_value(
                    &mut self.serial_active,
                    i,
                    format!(
                        "{} {}",
                        if self.serial_ui[i].connected {
                            "●"
                        } else {
                            "○"
                        },
                        i + 1
                    ),
                );
            }
        });
        let i = self.serial_active;
        let old = serde_json::to_string(&self.config.serial[i]).unwrap_or_default();
        ui.horizontal(|ui| {
            ui.label(tr(en, "Port", "Port"));
            ui.add(egui::TextEdit::singleline(&mut self.config.serial[i].port).desired_width(180.));
        });
        ui.horizontal(|ui| {
            egui::ComboBox::from_id_salt(("ports", i))
                .selected_text(tr(en, "Port listesi", "Port list"))
                .show_ui(ui, |ui| {
                    for p in &self.ports {
                        ui.selectable_value(&mut self.config.serial[i].port, p.clone(), p);
                    }
                });
            if ui.button(tr(en, "Yenile", "Refresh")).clicked() {
                self.ports = serial::ports();
            }
        });
        ui.horizontal(|ui| {
            ui.label("Baud");
            ui.add(egui::DragValue::new(&mut self.config.serial[i].baud).range(300..=2_000_000));
            ui.checkbox(&mut self.config.serial[i].dtr, "DTR");
            ui.checkbox(&mut self.config.serial[i].rts, "RTS");
        });
        ui.horizontal(|ui| {
            if action(
                ui,
                if self.serial[i].is_some() {
                    tr(en, "Bağlantıyı kes", "Disconnect")
                } else {
                    tr(en, "Bağlan", "Connect")
                },
                [47, 73, 81],
                !self.retired_serial.iter().any(|(index, _)| *index == i),
            ) {
                if let Some(s) = self.serial[i].take() {
                    s.send(SerialCommand::Stop);
                    self.retired_serial.push((i, s));
                } else {
                    self.connect_serial(i);
                }
            }
            if ui.button(tr(en, "Temizle", "Clear")).clicked() {
                self.serial_ui[i].logs.clear();
            }
            ui.checkbox(&mut self.serial_ui[i].autoscroll, tr(en, "İzle", "Follow"));
        });
        ui.horizontal_wrapped(|ui| {
            ui.checkbox(
                &mut self.config.serial[i].record,
                tr(en, "Kaydet", "Record"),
            );
            ui.checkbox(
                &mut self.config.serial[i].stamp,
                tr(en, "Saat", "Timestamp"),
            );
            ui.checkbox(&mut self.config.serial[i].plot, tr(en, "Grafik", "Plot"));
            ui.checkbox(
                &mut self.config.serial[i].auto,
                tr(en, "Otomatik bağlan", "Auto connect"),
            );
        });
        ui.label(RichText::new(&self.serial_ui[i].state).small());
        if old != serde_json::to_string(&self.config.serial[i]).unwrap_or_default()
            && let Some(s) = &self.serial[i]
        {
            s.send(SerialCommand::Configure(self.config.serial[i].clone()));
        }
        let h = (ui.available_height() - 220.).max(100.);
        egui::ScrollArea::both()
            .id_salt(("serial_log", i))
            .max_height(h)
            .min_scrolled_height(h)
            .stick_to_bottom(self.serial_ui[i].autoscroll)
            .show_rows(ui, 18., self.serial_ui[i].logs.len(), |ui, range| {
                for n in range {
                    ui.monospace(&self.serial_ui[i].logs[n]);
                }
            });
        ui.horizontal(|ui| {
            let response = ui.add(
                egui::TextEdit::singleline(&mut self.serial_ui[i].send)
                    .desired_width(ui.available_width() - 155.),
            );
            let enter = response.lost_focus() && ui.input(|i| i.key_pressed(egui::Key::Enter));
            egui::ComboBox::from_id_salt(("eol", i))
                .width(60.)
                .selected_text(["None", "LF", "CR+LF", "CR"][self.config.serial[i].eol.min(3)])
                .show_ui(ui, |ui| {
                    for (n, label) in ["None", "LF", "CR+LF", "CR"].iter().enumerate() {
                        ui.selectable_value(&mut self.config.serial[i].eol, n, *label);
                    }
                });
            if (ui.button(tr(en, "Gönder", "Send")).clicked() || enter) && self.serial[i].is_some()
            {
                self.serial[i]
                    .as_ref()
                    .unwrap()
                    .send(SerialCommand::Send(std::mem::take(
                        &mut self.serial_ui[i].send,
                    )));
            }
        });
        ui.separator();
        egui::ScrollArea::vertical()
            .id_salt(("serial_values", i))
            .max_height(130.)
            .show(ui, |ui| {
                for (key, stats) in &self.serial_ui[i].values {
                    let full = format!("Serial{}_{}", i + 1, key);
                    ui.horizontal(|ui| {
                        if let Some(series) = self.series.get_mut(&full) {
                            ui.checkbox(
                                &mut series.visible,
                                RichText::new(key).color(c(series.color)),
                            );
                        }
                        ui.label(format_value(stats.last, ""));
                        ui.small(format!(
                            "{} … {}",
                            format_value(stats.min, ""),
                            format_value(stats.max, "")
                        ));
                    });
                }
            });
    }
    fn live(&mut self, ui: &mut egui::Ui) {
        let en = self.config.english;
        egui::TopBottomPanel::top("live_stats")
            .resizable(true)
            .default_height(145.)
            .min_height(80.)
            .show_inside(ui, |ui| {
                stats_table(ui, &self.series, en);
            });
        egui::TopBottomPanel::bottom("live_log")
            .resizable(true)
            .default_height(130.)
            .min_height(65.)
            .show_inside(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.strong(tr(en, "Olaylar", "Events"));
                    if ui
                        .button(tr(en, "Ekranı temizle", "Clear display"))
                        .clicked()
                    {
                        self.logs.clear();
                    }
                });
                egui::ScrollArea::vertical()
                    .stick_to_bottom(true)
                    .show_rows(ui, 18., self.logs.len(), |ui, r| {
                        for i in r {
                            ui.monospace(&self.logs[i]);
                        }
                    });
            });
        ui.horizontal(|ui| {
            if action(
                ui,
                tr(en, "Grafiği temizle", "Clear chart"),
                [49, 68, 82],
                true,
            ) {
                for s in self.series.values_mut() {
                    s.clear();
                }
                self.events.retain(|e| e.kind != "mark");
                self.plot.follow = true;
            }
            ui.small(tr(
                en,
                "Sürükle: gezin · Tekerlek: zaman · Ctrl: Y · Çift tık: sıfırla",
                "Drag: pan · Wheel: time · Ctrl: Y · Double click: reset",
            ));
        });
        let events: Vec<_> = self.events.iter().cloned().collect();
        plot_view(
            ui,
            "live_plot",
            &mut self.series,
            &events,
            &mut self.plot,
            PlotOptions {
                en,
                clock: None,
                limits: if self.config.limit {
                    Some((self.config.low, self.config.high))
                } else {
                    None
                },
            },
        );
    }
    fn viewer(&mut self, ui: &mut egui::Ui) {
        let en = self.config.english;
        ui.horizontal_wrapped(|ui| {
            if ui
                .button(tr(
                    en,
                    "Kayıt / seri / log aç…",
                    "Open recording / serial / log…",
                ))
                .clicked()
                && let Some(files) = rfd::FileDialog::new()
                    .add_filter("ScopeRec", &["csv", "txt"])
                    .pick_files()
            {
                self.open_files(files);
            }
            if ui.button(tr(en, "PNG kaydet…", "Save PNG…")).clicked() {
                self.export(true);
            }
            if let Some(r) = &self.viewer {
                ui.label(r.path.file_name().unwrap_or_default().to_string_lossy());
            }
        });
        if self.viewer_rx.is_some() {
            ui.spinner();
            ui.label(tr(en, "Kayıt yükleniyor…", "Loading recording…"));
        }
        let Some(recording) = self.viewer.as_mut() else {
            ui.centered_and_justified(|ui| {
                ui.label(tr(
                    en,
                    "CSV veya seri kaydını sürükleyip bırakın.",
                    "Drop a CSV or serial recording here.",
                ));
            });
            return;
        };
        ui.collapsing(
            tr(en, "Kayıt istatistikleri", "Recording statistics"),
            |ui| {
                egui::ScrollArea::vertical()
                    .id_salt("viewer_statistics")
                    .max_height(160.)
                    .show(ui, |ui| stats_table(ui, &recording.series, en));
            },
        );
        ui.horizontal_wrapped(|ui| {
            egui::ComboBox::from_id_salt("analysis_key")
                .selected_text(&self.analysis_key)
                .show_ui(ui, |ui| {
                    for key in recording.series.keys() {
                        ui.selectable_value(&mut self.analysis_key, key.clone(), key);
                    }
                });
            numeric(ui, tr(en, "Alt", "Low"), &mut self.analysis_low);
            numeric(ui, tr(en, "Üst", "High"), &mut self.analysis_high);
            if ui
                .button(tr(en, "Limit analizi", "Analyze limits"))
                .clicked()
            {
                recording.analyze(&self.analysis_key, self.analysis_low, self.analysis_high);
            }
        });
        egui::TopBottomPanel::bottom("viewer_events")
            .resizable(true)
            .default_height(160.)
            .min_height(70.)
            .show_inside(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.label(tr(en, "Olay filtresi", "Event filter"));
                    ui.text_edit_singleline(&mut self.event_filter);
                });
                egui::ScrollArea::vertical().show(ui, |ui| {
                    for event in recording.events.iter().filter(|e| {
                        e.text
                            .to_lowercase()
                            .contains(&self.event_filter.to_lowercase())
                    }) {
                        let color = match event.kind.as_str() {
                            "error" | "limit" | "analysis" => Color32::from_rgb(210, 106, 116),
                            "mark" => Color32::from_rgb(99, 159, 226),
                            "recovery" => Color32::from_rgb(82, 180, 137),
                            _ => ui.visuals().text_color(),
                        };
                        if ui
                            .selectable_label(
                                false,
                                RichText::new(format!(
                                    "{:.2}s  +{:.2}s  {}  {}",
                                    event.t, event.duration, event.text, event.value
                                ))
                                .color(color),
                            )
                            .clicked()
                        {
                            let w = event.duration.max(10.);
                            self.viewer_plot.follow = false;
                            self.viewer_plot.request =
                                Some(((event.t - w * 0.2).max(0.), event.t + w * 1.2));
                        }
                    }
                });
            });
        plot_view(
            ui,
            "viewer_plot",
            &mut recording.series,
            &recording.events,
            &mut self.viewer_plot,
            PlotOptions {
                en,
                clock: recording.start_clock,
                limits: None,
            },
        );
    }
}

fn stats_table(ui: &mut egui::Ui, series: &BTreeMap<String, Series>, en: bool) {
    egui::ScrollArea::both().show(ui, |ui| {
        egui::Grid::new("stats_table")
            .striped(true)
            .min_col_width(110.)
            .show(ui, |ui| {
                for label in [
                    tr(en, "Seri", "Series"),
                    tr(en, "Son", "Last"),
                    "Min",
                    tr(en, "Maks", "Max"),
                    tr(en, "Ortalama", "Mean"),
                ] {
                    ui.strong(label);
                }
                ui.end_row();
                for s in series.values() {
                    ui.label(RichText::new(format!("● {}", s.key)).color(c(s.color)));
                    for v in [s.stats.last, s.stats.min, s.stats.max, s.stats.mean()] {
                        ui.monospace(format_value(v, &s.unit));
                    }
                    ui.end_row();
                }
            });
    });
}

impl eframe::App for ScopeApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.poll();
        if let Some(mut test) = self.endurance.take() {
            test.tick(self, ctx);
            self.endurance = Some(test);
        }
        let options = engine::LiveOptions::from(&self.config);
        if self.live_options.as_ref() != Some(&options) {
            self.engine.send(Command::Options(options.clone()));
            self.live_options = Some(options);
        }
        let en = self.config.english;
        let dropped = ctx.input(|i| {
            i.raw
                .dropped_files
                .iter()
                .filter_map(|f| f.path.clone())
                .collect::<Vec<_>>()
        });
        if !dropped.is_empty() {
            self.open_files(dropped);
        }
        if ctx.input(|i| i.viewport().close_requested())
            && (self.test_active || self.serial.iter().any(Option::is_some))
            && !self.closing
        {
            ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
            self.close_prompt = true;
        }
        if self.closing {
            if self.engine.finished()
                && self.serial.iter().flatten().all(SerialSession::finished)
                && self.retired_serial.iter().all(|(_, s)| s.finished())
            {
                ctx.send_viewport_cmd(egui::ViewportCommand::Close);
            } else {
                ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
            }
        }
        self.top(ctx);
        self.quick_actions(ctx);
        self.left(ctx);
        egui::TopBottomPanel::bottom("status").show(ctx, |ui| {
            ui.horizontal_wrapped(|ui| {
                ui.label(if self.closing {
                    tr(en, "Bağlantılar kapatılıyor…", "Closing connections…")
                } else {
                    &self.status
                });
                ui.separator();
                ui.label(format!(
                    "{}: {}   {}: {}",
                    tr(en, "Okuma", "Samples"),
                    self.samples,
                    tr(en, "Limit dışı", "Violations"),
                    self.violations
                ));
                if let Some(path) = &self.stem
                    && ui
                        .link(path.file_name().unwrap_or_default().to_string_lossy())
                        .clicked()
                {
                    platform::reveal(path.parent().unwrap_or(&self.config.directory));
                }
            });
        });
        if self.config.serial_side {
            egui::SidePanel::right("serial_side")
                .default_width(380.)
                .min_width(320.)
                .max_width(800.)
                .resizable(true)
                .show(ctx, |ui| {
                    self.serial_panel(ui);
                });
        }
        egui::CentralPanel::default().show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.selectable_value(&mut self.tab, 0, tr(en, "Canlı ölçüm", "Live measurement"));
                ui.selectable_value(&mut self.tab, 1, tr(en, "Seri port", "Serial port"));
                ui.selectable_value(
                    &mut self.tab,
                    2,
                    tr(en, "Kayıt görüntüleyici", "Recording viewer"),
                );
            });
            ui.separator();
            match self.tab {
                0 => self.live(ui),
                1 => {
                    if self.config.serial_side {
                        ui.label(tr(
                            en,
                            "Seri terminal sağ panelde açık.",
                            "Serial terminal is open in the right panel.",
                        ));
                        if ui.button(tr(en, "Sekmeye taşı", "Move to tab")).clicked() {
                            self.config.serial_side = false;
                        }
                    } else {
                        self.serial_panel(ui);
                    }
                }
                _ => self.viewer(ui),
            }
        });
        if let Some(message) = self.error.clone() {
            let mut open = true;
            egui::Window::new(tr(en, "Bilgi / hata", "Information / error"))
                .open(&mut open)
                .collapsible(false)
                .show(ctx, |ui| {
                    ui.label(message);
                });
            if !open {
                self.error = None;
            }
        }
        if self.close_prompt {
            egui::Window::new(tr(en, "Uygulamayı kapat", "Close application"))
                .collapsible(false)
                .resizable(false)
                .anchor(egui::Align2::CENTER_CENTER, Vec2::ZERO)
                .show(ctx, |ui| {
                    ui.label(tr(
                        en,
                        "Aktif ölçüm ve seri bağlantılar güvenle kapatılacak.",
                        "Active measurement and serial connections will be closed safely.",
                    ));
                    ui.horizontal(|ui| {
                        if ui.button(tr(en, "Kapat", "Close")).clicked() {
                            self.close_prompt = false;
                            self.closing = true;
                            self.engine.send(Command::Shutdown);
                            for s in self.serial.iter().flatten() {
                                s.send(SerialCommand::Stop);
                            }
                        }
                        if ui.button(tr(en, "Vazgeç", "Cancel")).clicked() {
                            self.close_prompt = false;
                        }
                    });
                });
        }
        self.sleep
            .set(self.running || self.serial_ui.iter().any(|s| s.connected));
        self.config.window = self.plot.window;
        self.config.selected_unit = self.plot.unit.clone();
        if !self.isolated && self.last_save.elapsed() > Duration::from_secs(3) {
            let json = serde_json::to_string(&self.config).unwrap_or_default();
            if json != self.saved {
                if let Err(e) = self.config.save() {
                    self.error = Some(e.to_string());
                } else {
                    self.saved = json;
                }
            }
            self.last_save = Instant::now();
        }
        ctx.request_repaint_after(if self.running || self.serial.iter().any(Option::is_some) {
            Duration::from_millis(33)
        } else {
            Duration::from_millis(250)
        });
    }
    fn on_exit(&mut self, _gl: Option<&eframe::glow::Context>) {
        if !self.isolated {
            let _ = self.config.save();
        }
        self.engine.send(Command::Shutdown);
        for serial in self.serial.iter().flatten() {
            serial.send(SerialCommand::Stop);
        }
        self.sleep.set(false);
    }
    fn raw_input_hook(&mut self, _ctx: &egui::Context, raw: &mut egui::RawInput) {
        if let Some(test) = self.endurance.as_mut() {
            test.input(raw, self.plot.screen);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn chart_renders_large_history_serial_axis_and_cursor_without_a_native_device() {
        let ctx = egui::Context::default();
        let mut series = BTreeMap::new();
        for (key, serial) in [("C1_RMS", false), ("C2_RMS", false), ("Serial1_T", true)] {
            let mut s = Series::new(key, serial);
            for i in 0..50000 {
                s.add(
                    i as f64 * 0.01,
                    if serial {
                        24. + (i as f64 / 300.).sin()
                    } else {
                        0.004 + (i as f64 / 100.).sin() * 0.001
                    },
                    100000,
                );
            }
            series.insert(key.into(), s);
        }
        let mut state = PlotState::default();
        let events = vec![Event {
            t: 450.,
            duration: 20.,
            kind: "analysis".into(),
            text: "limit test".into(),
            value: String::new(),
        }];
        for frame in 0..8 {
            let mut raw = egui::RawInput {
                screen_rect: Some(egui::Rect::from_min_size(
                    egui::Pos2::ZERO,
                    Vec2::new(1100., 760.),
                )),
                ..Default::default()
            };
            if state.screen.is_positive() {
                raw.events
                    .push(egui::Event::PointerMoved(state.screen.center()));
            }
            let output = ctx.run(raw, |ctx| {
                egui::CentralPanel::default().show(ctx, |ui| {
                    plot_view(
                        ui,
                        "test_plot",
                        &mut series,
                        &events,
                        &mut state,
                        PlotOptions {
                            en: frame % 2 == 0,
                            clock: None,
                            limits: Some((Some(0.002), Some(0.006))),
                        },
                    );
                });
            });
            assert!(!output.shapes.is_empty());
            assert!(state.bounds.iter().all(|v| v.is_finite()));
        }
        assert!(state.screen.width() > 300.);
        assert!(state.bounds[1] > state.bounds[0]);
        state.shift(-0.25);
        assert!(!state.follow && state.request.is_some());
    }
}

use crate::model::{Config, Event, Series, Stats, format_value};
use anyhow::{Context, Result, bail};
use chrono::{Local, NaiveDateTime, TimeZone};
use std::{
    collections::BTreeMap,
    fs::{File, OpenOptions},
    io::{BufRead, BufReader, BufWriter, Write},
    path::{Path, PathBuf},
    time::{Duration, Instant},
};

pub struct Recorder {
    pub stem: PathBuf,
    csv: csv::Writer<BufWriter<File>>,
    log: BufWriter<File>,
    events: csv::Writer<BufWriter<File>>,
    flushed: Instant,
}
fn create(path: impl AsRef<Path>) -> Result<File> {
    Ok(OpenOptions::new().write(true).create_new(true).open(path)?)
}
pub fn suffix(stem: &Path, ending: &str) -> PathBuf {
    PathBuf::from(format!("{}{ending}", stem.display()))
}
impl Recorder {
    pub fn new(config: &Config, keys: &[String]) -> Result<Self> {
        std::fs::create_dir_all(&config.directory)?;
        let prefix: String = config
            .test_name
            .chars()
            .map(|c| {
                if c.is_control() || "/\\:*?\"<>|".contains(c) {
                    '_'
                } else {
                    c
                }
            })
            .collect();
        let name = format!(
            "{}{}{}",
            prefix.trim(),
            if prefix.trim().is_empty() { "" } else { "_" },
            Local::now().format("%Y%m%d_%H%M%S_%3f")
        );
        let stem = config.directory.join(name);
        let mut file = create(suffix(&stem, ".csv"))?;
        file.write_all(b"\xef\xbb\xbf")?;
        let mut csv = csv::WriterBuilder::new()
            .delimiter(b';')
            .from_writer(BufWriter::new(file));
        let mut header = vec!["zaman".to_owned(), "t[s]".to_owned()];
        header.extend(
            keys.iter()
                .map(|k| format!("{k}[{}]", crate::model::unit(k))),
        );
        csv.write_record(header)?;
        let log = BufWriter::new(create(suffix(&stem, "_log.txt"))?);
        let mut events = csv::WriterBuilder::new()
            .delimiter(b';')
            .from_writer(BufWriter::new(create(suffix(&stem, "_olaylar.csv"))?));
        events.write_record(["zaman", "t[s]", "olay", "deger", "sure[s]", "kind"])?;
        let mut result = Self {
            stem,
            csv,
            log,
            events,
            flushed: Instant::now(),
        };
        result.flush()?;
        Ok(result)
    }
    pub fn sample(&mut self, t: f64, values: &[f64]) -> Result<()> {
        let mut row = vec![
            Local::now().format("%Y-%m-%d %H:%M:%S%.3f").to_string(),
            format!("{t:.6}"),
        ];
        row.extend(values.iter().map(|v| {
            if v.is_finite() {
                format!("{v:.12e}")
            } else {
                String::new()
            }
        }));
        self.csv.write_record(row)?;
        self.periodic_flush()
    }
    pub fn event(&mut self, event: &Event) -> Result<()> {
        let clock = Local::now().format("%Y-%m-%d %H:%M:%S%.3f").to_string();
        writeln!(self.log, "{clock}\t{:.6}\t{}", event.t, event.text)?;
        self.events.write_record([
            clock,
            format!("{:.6}", event.t),
            event.text.clone(),
            event.value.clone(),
            format!("{:.6}", event.duration),
            event.kind.clone(),
        ])?;
        self.periodic_flush()
    }
    pub fn summary(
        &mut self,
        t: f64,
        samples: u64,
        violations: u64,
        stats: &BTreeMap<String, Stats>,
    ) -> Result<()> {
        self.event(&Event::new(t,"summary",format!("Test bitti / Test complete: {t:.2}s, {samples} okuma / samples, {violations} limit dışı / violations")))?;
        for (key, s) in stats {
            let u = if key.starts_with("Serial") {
                String::new()
            } else {
                crate::model::unit(key)
            };
            self.event(&Event::new(
                t,
                "summary",
                format!(
                    "{key}: min {} / max {} / mean {} / n {}",
                    format_value(s.min, &u),
                    format_value(s.max, &u),
                    format_value(s.mean(), &u),
                    s.count
                ),
            ))?;
        }
        self.flush()
    }
    fn periodic_flush(&mut self) -> Result<()> {
        if self.flushed.elapsed() >= Duration::from_secs(1) {
            self.flush()?;
        }
        Ok(())
    }
    pub fn flush(&mut self) -> Result<()> {
        self.csv.flush()?;
        self.log.flush()?;
        self.events.flush()?;
        self.flushed = Instant::now();
        Ok(())
    }
}
impl Drop for Recorder {
    fn drop(&mut self) {
        let _ = self.flush();
    }
}

#[derive(Clone, Default)]
pub struct Recording {
    pub path: PathBuf,
    pub series: BTreeMap<String, Series>,
    pub events: Vec<Event>,
    pub start_clock: Option<f64>,
    pub offset: f64,
    pub duration: f64,
}
fn delimiter(line: &str) -> u8 {
    if line.contains(';') {
        b';'
    } else if line.contains('\t') {
        b'\t'
    } else {
        b','
    }
}
fn parse(v: &str, sep: u8) -> f64 {
    let v = if sep != b',' {
        v.trim().replace(',', ".")
    } else {
        v.trim().to_owned()
    };
    v.parse::<f64>()
        .ok()
        .filter(|x| x.is_finite() && x.abs() < 9e36)
        .unwrap_or(f64::NAN)
}
fn event_kind(text: &str) -> &'static str {
    let s = text.to_lowercase();
    if s.contains("normale") || s.contains("recover") {
        "recovery"
    } else if s.contains("limit") {
        "limit"
    } else if s.contains("isaret")
        || s.contains("işaret")
        || s.contains("şaret")
        || s.contains("mark")
    {
        "mark"
    } else if s.contains("hata") || s.contains("error") || s.contains("koptu") {
        "error"
    } else if s.contains("kesinti") || s.contains("gap") {
        "gap"
    } else {
        "info"
    }
}
fn timestamp(s: &str) -> Option<f64> {
    for fmt in [
        "%Y-%m-%d %H:%M:%S%.f",
        "%Y-%m-%d %H:%M:%S",
        "%d.%m.%Y %H:%M:%S",
        "%m/%d/%Y %I:%M:%S %p",
        "%m/%d/%Y %H:%M:%S",
        "%d/%m/%Y %H:%M:%S",
    ] {
        if let Ok(t) = NaiveDateTime::parse_from_str(s, fmt)
            && let Some(t) = Local.from_local_datetime(&t).earliest()
        {
            return Some(t.timestamp_millis() as f64 / 1000.);
        }
    }
    None
}
impl Recording {
    pub fn load(path: &Path) -> Result<Self> {
        let name = path.file_name().and_then(|s| s.to_str()).unwrap_or("");
        if let Some(stem) = name
            .strip_suffix("_log.txt")
            .or_else(|| name.strip_suffix("_olaylar.csv"))
        {
            let csv = path.with_file_name(format!("{stem}.csv"));
            if csv.exists() {
                return Self::load(&csv);
            }
            let mut out = Self {
                path: path.into(),
                ..Default::default()
            };
            out.add_events(path)?;
            out.duration = out
                .events
                .iter()
                .map(|e| e.t + e.duration)
                .fold(0., f64::max);
            return Ok(out);
        }
        if path.extension().is_some_and(|x| x == "txt") {
            let mut r = Self {
                path: path.into(),
                ..Default::default()
            };
            r.add_serial(path)?;
            r.find_gaps();
            return Ok(r);
        }
        let first = BufReader::new(File::open(path)?)
            .lines()
            .next()
            .context("empty CSV")??;
        let sep = delimiter(&first);
        let mut reader = csv::ReaderBuilder::new()
            .delimiter(sep)
            .flexible(true)
            .from_path(path)?;
        let header = reader.headers()?.clone();
        if header.len() < 3 {
            bail!("Measurement CSV needs clock, time and value columns");
        }
        let mut out = Self {
            path: path.into(),
            ..Default::default()
        };
        let keys: Vec<_> = header
            .iter()
            .skip(2)
            .map(|h| {
                h.trim()
                    .trim_start_matches('\u{feff}')
                    .split('[')
                    .next()
                    .unwrap_or(h)
                    .trim()
                    .replace(' ', "_")
            })
            .collect();
        for (key, h) in keys.iter().zip(header.iter().skip(2)) {
            let mut s = Series::new(key, false);
            s.unit = crate::model::unit(h);
            out.series.insert(key.clone(), s);
        }
        let mut first = true;
        for row in reader.records() {
            let row = row?;
            let t = parse(row.get(1).unwrap_or(""), sep);
            if !t.is_finite() {
                continue;
            }
            if first {
                out.offset = t;
                out.start_clock = row.get(0).and_then(timestamp);
                first = false;
            }
            let t = t - out.offset;
            if t < 0. {
                continue;
            }
            out.duration = out.duration.max(t);
            for (i, key) in keys.iter().enumerate() {
                out.series.get_mut(key).unwrap().add(
                    t,
                    parse(row.get(i + 2).unwrap_or(""), sep),
                    usize::MAX,
                );
            }
        }
        if first {
            bail!("CSV contains no valid measurement timestamps");
        }
        let stem = path.with_extension("");
        let events = suffix(&stem, "_olaylar.csv");
        if events.exists() {
            out.add_events(&events)?;
        }
        let log = suffix(&stem, "_log.txt");
        if log.exists() {
            out.add_events(&log)?;
        }
        for tag in ["_seri.txt", "_seri2.txt", "_seri3.txt"] {
            let file = suffix(&stem, tag);
            if file.exists() {
                out.add_serial(&file)?;
            }
        }
        out.find_gaps();
        out.events.sort_by(|a, b| a.t.total_cmp(&b.t));
        Ok(out)
    }
    pub fn add_events(&mut self, path: &Path) -> Result<()> {
        if path.extension().is_some_and(|x| x == "csv") {
            let first = BufReader::new(File::open(path)?)
                .lines()
                .next()
                .unwrap_or(Ok(String::new()))?;
            let sep = delimiter(&first);
            let mut r = csv::ReaderBuilder::new()
                .delimiter(sep)
                .flexible(true)
                .from_path(path)?;
            for row in r.records() {
                let row = row?;
                let t = parse(row.get(1).unwrap_or(""), sep) - self.offset;
                if !t.is_finite() {
                    continue;
                }
                let text = row.get(2).unwrap_or("").to_owned();
                let kind = row.get(5).unwrap_or_else(|| event_kind(&text));
                self.events.push(Event {
                    t,
                    duration: parse(row.get(4).unwrap_or("0"), sep).max(0.),
                    kind: kind.into(),
                    text,
                    value: row.get(3).unwrap_or("").into(),
                });
            }
        } else {
            for line in BufReader::new(File::open(path)?).lines() {
                let line = line?;
                let parts: Vec<_> = line.splitn(3, '\t').collect();
                if parts.len() == 3 {
                    let t = parse(parts[1], b'\t') - self.offset;
                    if t.is_finite() {
                        self.events
                            .push(Event::new(t, event_kind(parts[2]), parts[2]));
                    }
                }
            }
        }
        self.events.sort_by(|a, b| a.t.total_cmp(&b.t));
        self.events.dedup_by(|a, b| {
            if (a.t - b.t).abs() > 0.01 {
                return false;
            }
            if a.text == b.text {
                return true;
            }
            if a.kind == "mark" && b.kind == "mark" {
                if a.text.len() > b.text.len() {
                    b.text = a.text.clone();
                }
                return true;
            }
            false
        });
        Ok(())
    }
    pub fn add_serial(&mut self, path: &Path) -> Result<()> {
        let name = path
            .file_stem()
            .and_then(|s| s.to_str())
            .unwrap_or("serial");
        let session = if name.ends_with("seri2") {
            2
        } else if name.ends_with("seri3") {
            3
        } else {
            1
        };
        let mut offset = self.offset;
        let mut first = self.series.is_empty();
        for line in BufReader::new(File::open(path)?).lines() {
            let line = line?;
            let parts: Vec<_> = line.splitn(3, '\t').collect();
            if parts.len() < 3 {
                continue;
            }
            let raw = parse(parts[1], b'\t');
            if !raw.is_finite() {
                continue;
            }
            if first {
                offset = raw;
                self.offset = raw;
                self.start_clock = timestamp(parts[0]);
                first = false;
            }
            let t = raw - offset;
            for (key, value) in crate::serial::parse_values(parts[2]) {
                let key = format!("Serial{session}_{key}");
                let s = self
                    .series
                    .entry(key.clone())
                    .or_insert_with(|| Series::new(&key, true));
                s.add(t, value, usize::MAX);
                self.duration = self.duration.max(t);
            }
        }
        Ok(())
    }
    pub fn find_gaps(&mut self) {
        let Some(series) = self.series.values().next() else {
            return;
        };
        if series.points.len() < 3 {
            return;
        }
        let mut intervals: Vec<_> = series
            .points
            .iter()
            .zip(series.points.iter().skip(1))
            .take(2000)
            .map(|(a, b)| b[0] - a[0])
            .filter(|x| *x > 0.)
            .collect();
        intervals.sort_by(f64::total_cmp);
        if intervals.is_empty() {
            return;
        }
        let limit = (intervals[intervals.len() / 2] * 5.).max(2.);
        for (a, b) in series.points.iter().zip(series.points.iter().skip(1)) {
            if b[0] - a[0] > limit {
                self.events.push(Event {
                    t: a[0],
                    duration: b[0] - a[0],
                    kind: "gap".into(),
                    text: "Veri kesintisi / Data gap".into(),
                    value: String::new(),
                });
            }
        }
    }
    pub fn analyze(&mut self, key: &str, low: Option<f64>, high: Option<f64>) {
        self.events.retain(|e| e.kind != "analysis");
        let Some(s) = self.series.get(key) else {
            return;
        };
        let mut open = None;
        for (i, p) in s.points.iter().enumerate() {
            let bad = !p[1].is_finite()
                || low.is_some_and(|v| p[1] < v)
                || high.is_some_and(|v| p[1] > v);
            if bad && open.is_none() {
                open = Some(p[0]);
            }
            if (!bad || i + 1 == s.points.len()) && open.is_some() {
                let t = open.take().unwrap();
                self.events.push(Event {
                    t,
                    duration: p[0] - t,
                    kind: "analysis".into(),
                    text: format!("Limit dışı / Out of limit: {key}"),
                    value: format_value(p[1], &s.unit),
                });
            }
        }
        self.events.sort_by(|a, b| a.t.total_cmp(&b.t));
    }
}

pub fn export_png(
    path: &Path,
    series: &BTreeMap<String, Series>,
    range: Option<(f64, f64)>,
    dark: bool,
    selected_unit: Option<&str>,
) -> Result<()> {
    use plotters::prelude::*;
    let area = BitMapBackend::new(path, (1800, 1000)).into_drawing_area();
    let bg = if dark { RGBColor(12, 21, 33) } else { WHITE };
    let fg = if dark { RGBColor(219, 226, 234) } else { BLACK };
    area.fill(&bg).map_err(|e| anyhow::anyhow!("{e}"))?;
    let visible: Vec<_> = series
        .values()
        .filter(|s| {
            s.visible
                && !s.points.is_empty()
                && (s.serial || selected_unit.is_none_or(|u| s.unit == u))
        })
        .collect();
    let max_t = visible
        .iter()
        .filter_map(|s| s.points.back().map(|p| p[0]))
        .fold(5f64, f64::max);
    let (lo, hi) = range.unwrap_or((0., max_t));
    let mut ymin = f64::INFINITY;
    let mut ymax = f64::NEG_INFINITY;
    let mut rmin = f64::INFINITY;
    let mut rmax = f64::NEG_INFINITY;
    for s in &visible {
        for p in &s.points {
            if p[0] >= lo && p[0] <= hi && p[1].is_finite() {
                if s.serial {
                    rmin = rmin.min(p[1]);
                    rmax = rmax.max(p[1]);
                } else {
                    ymin = ymin.min(p[1]);
                    ymax = ymax.max(p[1]);
                }
            }
        }
    }
    if !ymin.is_finite() {
        ymin = -1.;
        ymax = 1.;
    }
    let pad = ((ymax - ymin) * 0.08).max(ymax.abs() * 0.01).max(1e-9);
    if !rmin.is_finite() {
        rmin = -1.;
        rmax = 1.;
    }
    let rpad = ((rmax - rmin) * 0.08).max(rmax.abs() * 0.01).max(1e-9);
    let has_serial = visible.iter().any(|s| s.serial);
    let mut chart = ChartBuilder::on(&area)
        .caption("ScopeRec Rust", ("sans-serif", 28).into_font().color(&fg))
        .margin(25)
        .x_label_area_size(55)
        .y_label_area_size(100)
        .right_y_label_area_size(if has_serial { 90 } else { 0 })
        .build_cartesian_2d(lo..hi.max(lo + 0.001), (ymin - pad)..(ymax + pad))
        .map_err(|e| anyhow::anyhow!("{e}"))?
        .set_secondary_coord(lo..hi.max(lo + 0.001), (rmin - rpad)..(rmax + rpad));
    chart
        .configure_mesh()
        .x_desc("Time [s]")
        .axis_style(fg)
        .label_style(("sans-serif", 18).into_font().color(&fg))
        .light_line_style(if dark {
            RGBColor(30, 43, 57)
        } else {
            RGBColor(230, 233, 237)
        })
        .draw()
        .map_err(|e| anyhow::anyhow!("{e}"))?;
    if has_serial {
        chart
            .configure_secondary_axes()
            .y_desc("Serial")
            .axis_style(fg)
            .label_style(("sans-serif", 16).into_font().color(&fg))
            .draw()
            .map_err(|e| anyhow::anyhow!("{e}"))?;
    }
    for s in visible {
        let c = RGBColor(s.color[0], s.color[1], s.color[2]);
        let points = s.envelope(lo, hi, 1800);
        let mut run = vec![];
        for p in points.into_iter().chain(std::iter::once([0., f64::NAN])) {
            if !p[1].is_finite() {
                if !run.is_empty() {
                    let line = LineSeries::new(std::mem::take(&mut run), c.stroke_width(2));
                    if s.serial {
                        chart
                            .draw_secondary_series(line)
                            .map_err(|e| anyhow::anyhow!("{e}"))?;
                    } else {
                        chart
                            .draw_series(line)
                            .map_err(|e| anyhow::anyhow!("{e}"))?;
                    }
                }
                continue;
            }
            run.push((p[0], p[1]));
        }
        chart
            .draw_series(std::iter::once(PathElement::new(
                vec![(lo, ymin - pad), (lo, ymin - pad)],
                c,
            )))
            .map_err(|e| anyhow::anyhow!("{e}"))?
            .label(format!("{} [{}]", s.key, s.unit))
            .legend(move |(x, y)| PathElement::new(vec![(x, y), (x + 24, y)], c.stroke_width(2)));
    }
    chart
        .configure_series_labels()
        .background_style(bg.mix(0.92))
        .border_style(fg)
        .label_font(("sans-serif", 16).into_font().color(&fg))
        .draw()
        .map_err(|e| anyhow::anyhow!("{e}"))?;
    area.present().map_err(|e| anyhow::anyhow!("{e}"))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn recording_roundtrip_and_limit_analysis() {
        let tmp = tempfile::tempdir().unwrap();
        let c = Config {
            directory: tmp.path().into(),
            test_name: "test".into(),
            ..Default::default()
        };
        let keys = c.keys();
        let mut w = Recorder::new(&c, &keys).unwrap();
        for i in 0..100 {
            w.sample(i as f64 * 0.1, &[i as f64, 1.]).unwrap();
        }
        w.event(&Event::new(2., "mark", "İşaret / Mark")).unwrap();
        let path = suffix(&w.stem, ".csv");
        drop(w);
        let mut r = Recording::load(&path).unwrap();
        assert_eq!(r.series["C1_RMS"].points.len(), 100);
        assert_eq!(r.events.len(), 1);
        r.analyze("C1_RMS", Some(10.), Some(90.));
        assert_eq!(r.events.iter().filter(|e| e.kind == "analysis").count(), 2);
        assert_eq!(r.duration, 9.9);
    }
    #[test]
    fn legacy_csv_and_three_companion_serial_files_and_png() {
        let tmp = tempfile::tempdir().unwrap();
        let path = tmp.path().join("legacy.csv");
        std::fs::write(&path,"zaman;t[s];C1_RMS[V];C1_PKPK[V]\n2026-11-28 12:00:00;20;0,004;0,016\n2026-11-28 12:00:01;21;0,005;0,02\n").unwrap();
        for tag in ["_seri.txt", "_seri2.txt", "_seri3.txt"] {
            std::fs::write(
                suffix(&path.with_extension(""), tag),
                "2026-11-28 12:00:00\t20\tT:24,ACT:1500\n2026-11-28 12:00:01\t21\tT:25,ACT:1501\n",
            )
            .unwrap();
        }
        let recording = Recording::load(&path).unwrap();
        assert_eq!(recording.series.len(), 8);
        assert_eq!(recording.offset, 20.);
        assert_eq!(recording.series["C1_RMS"].points[0], [0., 0.004]);
        assert!(recording.series.contains_key("Serial3_T"));
        let log = suffix(&path.with_extension(""), "_log.txt");
        std::fs::write(&log, "2026-11-28 12:00:00\t20\tİşaret: test\n").unwrap();
        let from_log = Recording::load(&log).unwrap();
        assert_eq!(from_log.path, path);
        assert_eq!(from_log.series["Serial3_T"].points.len(), 2);
        assert!(from_log.events.iter().any(|e| e.kind == "mark"));
        let png = tmp.path().join("chart.png");
        export_png(&png, &recording.series, None, true, Some("V")).unwrap();
        let img = image::open(png).unwrap();
        assert_eq!((img.width(), img.height()), (1800, 1000));
    }
}

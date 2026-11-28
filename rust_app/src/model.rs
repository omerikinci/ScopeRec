use serde::{Deserialize, Serialize};
use std::{collections::VecDeque, path::PathBuf};

#[derive(Clone, Copy, Debug)]
pub struct Parameter {
    pub code: &'static str,
    pub tr: &'static str,
    pub en: &'static str,
    pub unit: &'static str,
}
pub const PARAMS: [Parameter; 17] = [
    Parameter {
        code: "RMS",
        tr: "RMS",
        en: "RMS",
        unit: "V",
    },
    Parameter {
        code: "MEAN",
        tr: "Ortalama",
        en: "Mean",
        unit: "V",
    },
    Parameter {
        code: "PKPK",
        tr: "Tepe-tepe",
        en: "Peak-to-peak",
        unit: "V",
    },
    Parameter {
        code: "MAX",
        tr: "Maksimum",
        en: "Maximum",
        unit: "V",
    },
    Parameter {
        code: "MIN",
        tr: "Minimum",
        en: "Minimum",
        unit: "V",
    },
    Parameter {
        code: "AMPL",
        tr: "Genlik",
        en: "Amplitude",
        unit: "V",
    },
    Parameter {
        code: "TOP",
        tr: "Üst seviye",
        en: "Top level",
        unit: "V",
    },
    Parameter {
        code: "BASE",
        tr: "Alt seviye",
        en: "Base level",
        unit: "V",
    },
    Parameter {
        code: "CRMS",
        tr: "Çevrim RMS",
        en: "Cycle RMS",
        unit: "V",
    },
    Parameter {
        code: "CMEAN",
        tr: "Çevrim ortalaması",
        en: "Cycle mean",
        unit: "V",
    },
    Parameter {
        code: "FREQ",
        tr: "Frekans",
        en: "Frequency",
        unit: "Hz",
    },
    Parameter {
        code: "PER",
        tr: "Periyot",
        en: "Period",
        unit: "s",
    },
    Parameter {
        code: "DUTY",
        tr: "Doluluk",
        en: "Duty cycle",
        unit: "%",
    },
    Parameter {
        code: "PWID",
        tr: "Pozitif darbe",
        en: "Positive width",
        unit: "s",
    },
    Parameter {
        code: "NWID",
        tr: "Negatif darbe",
        en: "Negative width",
        unit: "s",
    },
    Parameter {
        code: "RISE",
        tr: "Yükselme",
        en: "Rise time",
        unit: "s",
    },
    Parameter {
        code: "FALL",
        tr: "Düşme",
        en: "Fall time",
        unit: "s",
    },
];
pub fn unit(key: &str) -> String {
    if let Some((_, tail)) = key.rsplit_once('[') {
        return tail.trim_end_matches(']').to_owned();
    }
    PARAMS
        .iter()
        .find(|p| key.ends_with(p.code))
        .map_or("", |p| p.unit)
        .to_owned()
}
pub const COLORS: [[u8; 3]; 12] = [
    [62, 145, 235],
    [221, 157, 53],
    [61, 166, 119],
    [213, 89, 106],
    [163, 123, 221],
    [44, 170, 185],
    [210, 124, 68],
    [146, 165, 71],
    [105, 152, 181],
    [199, 109, 169],
    [122, 179, 157],
    [185, 159, 132],
];
pub fn color(key: &str) -> [u8; 3] {
    let index = if let Some((ch, code)) = key.split_once('_') {
        if let (Some(c), Some(p)) = (
            ch.strip_prefix('C').and_then(|x| x.parse::<usize>().ok()),
            PARAMS.iter().position(|p| p.code == code),
        ) {
            c.saturating_sub(1).wrapping_mul(17).wrapping_add(p)
        } else {
            key.bytes()
                .fold(0usize, |a, b| a.wrapping_mul(31).wrapping_add(b as usize))
        }
    } else {
        key.bytes()
            .fold(0usize, |a, b| a.wrapping_mul(31).wrapping_add(b as usize))
    };
    if index < COLORS.len() {
        return COLORS[index];
    }
    let hue = ((index % 4093) as f64 * 137.507764).rem_euclid(360.) / 60.;
    let chroma = 0.62 * 0.86;
    let x = chroma * (1. - (hue % 2. - 1.).abs());
    let m = 0.86 - chroma;
    let (r, g, b) = match hue as u8 {
        0 => (chroma, x, 0.),
        1 => (x, chroma, 0.),
        2 => (0., chroma, x),
        3 => (0., x, chroma),
        4 => (x, 0., chroma),
        _ => (chroma, 0., x),
    };
    [
        ((r + m) * 255.) as u8,
        ((g + m) * 255.) as u8,
        ((b + m) * 255.) as u8,
    ]
}

#[derive(Clone, Copy, Default, Debug, Serialize, Deserialize, PartialEq)]
pub enum Connection {
    #[default]
    Simulator,
    Lan,
    Usb,
}
#[derive(Clone, Copy, Default, Debug, Serialize, Deserialize, PartialEq)]
pub enum Dialect {
    #[default]
    Auto,
    Siglent,
    Rigol,
    Keysight,
    Tektronix,
    Generic,
}
impl Dialect {
    pub const ALL: [Self; 6] = [
        Self::Auto,
        Self::Siglent,
        Self::Rigol,
        Self::Keysight,
        Self::Tektronix,
        Self::Generic,
    ];
    pub fn label(self) -> &'static str {
        match self {
            Self::Auto => "Auto",
            Self::Siglent => "Siglent",
            Self::Rigol => "Rigol",
            Self::Keysight => "Keysight / Agilent",
            Self::Tektronix => "Tektronix",
            Self::Generic => "SCPI",
        }
    }
    pub fn detect(self, id: &str) -> Self {
        if self != Self::Auto {
            return self;
        }
        let id = id.to_uppercase();
        if id.contains("SIGLENT") {
            Self::Siglent
        } else if id.contains("RIGOL") {
            Self::Rigol
        } else if id.contains("KEYSIGHT") || id.contains("AGILENT") {
            Self::Keysight
        } else if id.contains("TEKTRONIX") {
            Self::Tektronix
        } else {
            Self::Generic
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct SerialConfig {
    pub port: String,
    pub baud: u32,
    pub record: bool,
    pub stamp: bool,
    pub dtr: bool,
    pub rts: bool,
    pub auto: bool,
    pub eol: usize,
    pub plot: bool,
}
impl Default for SerialConfig {
    fn default() -> Self {
        Self {
            port: "sim://serial".into(),
            baud: 115200,
            record: false,
            stamp: true,
            dtr: false,
            rts: false,
            auto: false,
            eol: 2,
            plot: true,
        }
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct Config {
    pub connection: Connection,
    pub address: String,
    pub usb: String,
    pub dialect: Dialect,
    pub channels: [bool; 4],
    pub params: Vec<String>,
    pub interval_ms: u64,
    pub dark: bool,
    pub english: bool,
    pub serial_side: bool,
    pub serial: [SerialConfig; 3],
    pub record: bool,
    pub directory: PathBuf,
    pub test_name: String,
    pub limit: bool,
    pub limit_series: String,
    pub low: Option<f64>,
    pub high: Option<f64>,
    pub sound: bool,
    pub window: f64,
    pub retention: usize,
    pub selected_unit: String,
}
impl Default for Config {
    fn default() -> Self {
        Self {
            connection: Connection::Simulator,
            address: "127.0.0.1:17025".into(),
            usb: String::new(),
            dialect: Dialect::Auto,
            channels: [true, false, false, false],
            params: vec!["RMS".into(), "PKPK".into()],
            interval_ms: 250,
            dark: true,
            english: false,
            serial_side: false,
            serial: std::array::from_fn(|_| SerialConfig::default()),
            record: false,
            directory: home().join("Documents/ScopeRec Rust"),
            test_name: String::new(),
            limit: false,
            limit_series: "C1_RMS".into(),
            low: None,
            high: None,
            sound: true,
            window: 120.0,
            retention: 200_000,
            selected_unit: "V".into(),
        }
    }
}
impl Config {
    pub fn keys(&self) -> Vec<String> {
        (0..4)
            .filter(|&i| self.channels[i])
            .flat_map(|i| self.params.iter().map(move |p| format!("C{}_{p}", i + 1)))
            .collect()
    }
    pub fn load() -> Self {
        std::fs::read(settings_path())
            .ok()
            .and_then(|s| serde_json::from_slice(&s).ok())
            .unwrap_or_default()
    }
    pub fn save(&self) -> anyhow::Result<()> {
        let p = settings_path();
        let tmp = p.with_extension("json.tmp");
        std::fs::write(&tmp, serde_json::to_vec_pretty(self)?)?;
        if cfg!(windows) && p.exists() {
            std::fs::remove_file(&p)?;
        }
        std::fs::rename(tmp, p)?;
        Ok(())
    }
}
pub fn home() -> PathBuf {
    std::env::var_os("HOME")
        .or_else(|| std::env::var_os("USERPROFILE"))
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
}
pub fn settings_path() -> PathBuf {
    home().join(".scoperec_rust.json")
}

#[derive(Clone, Debug)]
pub struct Stats {
    pub last: f64,
    pub min: f64,
    pub max: f64,
    pub sum: f64,
    pub count: u64,
}
impl Default for Stats {
    fn default() -> Self {
        Self {
            last: f64::NAN,
            min: f64::INFINITY,
            max: f64::NEG_INFINITY,
            sum: 0.0,
            count: 0,
        }
    }
}
impl Stats {
    pub fn add(&mut self, v: f64) {
        self.last = v;
        if v.is_finite() {
            self.min = self.min.min(v);
            self.max = self.max.max(v);
            self.sum += v;
            self.count += 1;
        }
    }
    pub fn mean(&self) -> f64 {
        if self.count == 0 {
            f64::NAN
        } else {
            self.sum / self.count as f64
        }
    }
}
#[derive(Clone, Debug)]
pub struct Series {
    pub key: String,
    pub unit: String,
    pub color: [u8; 3],
    pub points: VecDeque<[f64; 2]>,
    pub stats: Stats,
    pub visible: bool,
    pub serial: bool,
    pub revision: u64,
}
impl Series {
    pub fn new(key: &str, serial: bool) -> Self {
        Self {
            key: key.into(),
            unit: if serial { String::new() } else { unit(key) },
            color: color(key),
            points: VecDeque::new(),
            stats: Stats::default(),
            visible: true,
            serial,
            revision: 0,
        }
    }
    pub fn add(&mut self, t: f64, v: f64, cap: usize) {
        let cap = cap.max(100);
        if self.points.len() >= cap {
            self.points.drain(..(cap / 10).max(1));
        }
        self.points.push_back([t, v]);
        self.stats.add(v);
        self.revision += 1;
    }
    pub fn clear(&mut self) {
        self.points.clear();
        self.revision += 1;
    }
    pub fn nearest(&self, t: f64) -> Option<[f64; 2]> {
        let i = self.points.partition_point(|p| p[0] < t);
        [i.checked_sub(1), Some(i)]
            .into_iter()
            .flatten()
            .filter_map(|j| self.points.get(j))
            .min_by(|a, b| (a[0] - t).abs().total_cmp(&(b[0] - t).abs()))
            .copied()
    }
    /// Visible-range min/max envelope preserves spikes, gaps and temporal ordering.
    pub fn envelope(&self, lo: f64, hi: f64, pixels: usize) -> Vec<[f64; 2]> {
        let start = self.points.partition_point(|p| p[0] < lo).saturating_sub(1);
        let end = (self.points.partition_point(|p| p[0] <= hi) + 1).min(self.points.len());
        let n = end.saturating_sub(start);
        let bucket = (n / (pixels.max(100) * 2)).max(1);
        if bucket == 1 {
            return self.points.range(start..end).copied().collect();
        }
        let mut out = Vec::with_capacity(pixels * 4);
        let mut i = start;
        while i < end {
            let j = (i + bucket).min(end);
            let mut min = i;
            let mut max = i;
            let mut gap = None;
            for k in i..j {
                let v = self.points[k][1];
                if !v.is_finite() {
                    gap = Some(k);
                    continue;
                }
                if !self.points[min][1].is_finite() || v < self.points[min][1] {
                    min = k;
                }
                if !self.points[max][1].is_finite() || v > self.points[max][1] {
                    max = k;
                }
            }
            let mut indices = vec![i, min, max, j - 1];
            if let Some(k) = gap {
                indices.push(k);
            }
            indices.sort_unstable();
            indices.dedup();
            for k in indices {
                out.push(self.points[k]);
            }
            i = j;
        }
        out
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Event {
    pub t: f64,
    pub duration: f64,
    pub kind: String,
    pub text: String,
    pub value: String,
}
impl Event {
    pub fn new(t: f64, kind: &str, text: impl Into<String>) -> Self {
        Self {
            t,
            duration: 0.0,
            kind: kind.into(),
            text: text.into(),
            value: String::new(),
        }
    }
}
pub fn format_value(v: f64, unit: &str) -> String {
    if !v.is_finite() {
        return "—".into();
    }
    let a = v.abs();
    let (k, p) = if unit == "%" || unit.is_empty() || a == 0.0 {
        (1.0, "")
    } else if a >= 1e6 {
        (1e-6, "M")
    } else if a >= 1e3 {
        (1e-3, "k")
    } else if a >= 1.0 {
        (1.0, "")
    } else if a >= 1e-3 {
        (1e3, "m")
    } else if a >= 1e-6 {
        (1e6, "µ")
    } else {
        (1e9, "n")
    };
    let s = format!("{:.6}", v * k);
    format!(
        "{} {p}{unit}",
        s.trim_end_matches('0').trim_end_matches('.')
    )
    .trim()
    .to_owned()
}
pub fn parse_duration(s: &str) -> Option<f64> {
    let s = s.trim().to_lowercase().replace(',', ".");
    let split = s
        .find(|c: char| !c.is_ascii_digit() && c != '.' && c != ' ')
        .unwrap_or(s.len());
    let v = s[..split].trim().parse::<f64>().ok()?;
    let suffix = s[split..].trim();
    let scale = match suffix {
        "" | "s" | "sn" | "sec" => 1.,
        "dk" | "min" => 60.,
        "h" | "saat" => 3600.,
        "d" | "gün" | "gun" | "day" => 86400.,
        _ => return None,
    };
    if v.is_finite() && v > 0. {
        Some((v * scale).clamp(5., 864000.))
    } else {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn stats_and_clear() {
        let mut s = Series::new("C1_RMS", false);
        for i in 0..1000 {
            s.add(i as f64, i as f64, 100);
        }
        assert!(s.points.len() <= 100);
        assert_eq!(s.stats.count, 1000);
        s.clear();
        assert_eq!(s.stats.mean(), 499.5);
        assert!(s.points.is_empty());
    }
    #[test]
    fn envelope_preserves_spike_and_gap() {
        let mut s = Series::new("C1_RMS", false);
        for i in 0..10000 {
            s.add(
                i as f64,
                if i == 5012 {
                    100.
                } else if i == 6001 {
                    f64::NAN
                } else {
                    0.
                },
                20000,
            );
        }
        let p = s.envelope(0., 10000., 100);
        assert!(p.iter().any(|p| p[1] == 100.));
        assert!(p.iter().any(|p| p[1].is_nan()));
        assert!(p.windows(2).all(|w| w[0][0] <= w[1][0]));
        assert!(p.len() < 1000);
    }
    #[test]
    fn duration_and_units() {
        assert_eq!(parse_duration("1,5 saat"), Some(5400.));
        assert_eq!(parse_duration("2 gün"), Some(172800.));
        assert_eq!(format_value(0.004, "V"), "4 mV");
        assert_eq!(unit("C1_FREQ"), "Hz");
    }
    #[test]
    fn channel_measurement_colors_are_stable_and_distinct() {
        let mut colors = std::collections::BTreeSet::new();
        for ch in 1..=4 {
            for param in PARAMS {
                let key = format!("C{ch}_{}", param.code);
                assert_eq!(Series::new(&key, false).color, color(&key));
                colors.insert(color(&key));
            }
        }
        assert_eq!(colors.len(), 68);
        let _ = color("C0_RMS");
    }
}

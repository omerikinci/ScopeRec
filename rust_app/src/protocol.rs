use crate::{model::Dialect, transport::Instrument};
use anyhow::Result;
use std::sync::OnceLock;
pub fn number(raw: &str) -> f64 {
    if raw.contains('*') {
        return f64::NAN;
    }
    static RE: OnceLock<regex::Regex> = OnceLock::new();
    let re =
        RE.get_or_init(|| regex::Regex::new(r"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?").unwrap());
    re.find_iter(raw)
        .last()
        .and_then(|m| m.as_str().parse::<f64>().ok())
        .filter(|v| v.is_finite() && v.abs() <= 9e36)
        .unwrap_or(f64::NAN)
}
pub fn parse_pava(raw: &str, codes: &[String]) -> Vec<f64> {
    let tail = raw
        .rsplit_once("PAVA")
        .map_or(raw, |(_, s)| s)
        .trim_matches([' ', ':']);
    let parts: Vec<_> = tail.split(',').map(str::trim).collect();
    codes
        .iter()
        .map(|code| {
            parts
                .as_chunks::<2>()
                .0
                .iter()
                .find(|p| p[0].eq_ignore_ascii_case(code))
                .map_or(f64::NAN, |p| number(p[1]))
        })
        .collect()
}
pub fn command(dialect: Dialect, ch: usize, code: &str) -> Option<(String, f64)> {
    let item = match dialect {
        Dialect::Rigol => match code {
            "RMS" => "VRMS",
            "MEAN" => "VAVG",
            "PKPK" => "VPP",
            "MAX" => "VMAX",
            "MIN" => "VMIN",
            "AMPL" => "VAMP",
            "TOP" => "VTOP",
            "BASE" => "VBASe",
            "CRMS" => "PVRMS",
            "FREQ" => "FREQuency",
            "PER" => "PERiod",
            "DUTY" => "PDUTy",
            "PWID" => "PWIDth",
            "NWID" => "NWIDth",
            "RISE" => "RTIMe",
            "FALL" => "FTIMe",
            _ => return None,
        },
        Dialect::Tektronix => match code {
            "RMS" => "RMS",
            "MEAN" => "MEAN",
            "PKPK" => "PK2pk",
            "MAX" => "MAXImum",
            "MIN" => "MINImum",
            "AMPL" => "AMPlitude",
            "TOP" => "HIGH",
            "BASE" => "LOW",
            "CRMS" => "CRMs",
            "CMEAN" => "CMEan",
            "FREQ" => "FREQuency",
            "PER" => "PERIod",
            "DUTY" => "PDUty",
            "PWID" => "PWIdth",
            "NWID" => "NWIdth",
            "RISE" => "RISe",
            "FALL" => "FALL",
            _ => return None,
        },
        _ => match code {
            "RMS" => "VRMS",
            "MEAN" => "VAVerage",
            "PKPK" => "VPP",
            "MAX" => "VMAX",
            "MIN" => "VMIN",
            "AMPL" => "VAMPlitude",
            "TOP" => "VTOP",
            "BASE" => "VBASe",
            "FREQ" => "FREQuency",
            "PER" => "PERiod",
            "DUTY" => "DUTYcycle",
            "PWID" => "PWIDth",
            "NWID" => "NWIDth",
            "RISE" => "RISetime",
            "FALL" => "FALLtime",
            _ => return None,
        },
    };
    Some((
        match dialect {
            Dialect::Rigol => format!(":MEASure:ITEM? {item},CHANnel{ch}"),
            Dialect::Tektronix => format!(
                "MEASUrement:IMMed:SOUrce1 CH{ch};:MEASUrement:IMMed:TYPe {item};:MEASUrement:IMMed:VALue?"
            ),
            _ => format!(":MEASure:{item}? CHANnel{ch}"),
        },
        if dialect == Dialect::Rigol && code == "DUTY" {
            100.
        } else {
            1.
        },
    ))
}
pub fn measure(
    client: &mut dyn Instrument,
    dialect: Dialect,
    ch: usize,
    codes: &[String],
) -> Result<Vec<f64>> {
    if dialect == Dialect::Siglent {
        let response = client.text(&format!(
            "C{ch}:PAVA? {}",
            if codes.len() == 1 {
                codes[0].as_str()
            } else {
                "ALL"
            }
        ))?;
        return Ok(parse_pava(&response, codes));
    }
    codes
        .iter()
        .map(|code| {
            if let Some((command, scale)) = command(dialect, ch, code) {
                Ok(number(&client.text(&command)?) * scale)
            } else {
                Ok(f64::NAN)
            }
        })
        .collect()
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn pava_preserves_measurement_identity() {
        let codes = vec!["RMS".into(), "MEAN".into(), "PKPK".into()];
        let v = parse_pava("C1:PAVA MEAN,-2.5E-3,PKPK,1.6E-2,RMS,8E-3,CRMS,99", &codes);
        assert_eq!(v, [-0.0 + 0.008, -0.0025, 0.016]);
        assert!(number("****").is_nan());
        assert!(number("9.9e37").is_nan());
        assert_eq!(number("FREQ,1.2E+3Hz"), 1200.);
    }
    #[test]
    fn vendor_commands() {
        assert!(
            command(Dialect::Rigol, 2, "RMS")
                .unwrap()
                .0
                .contains("CHANnel2")
        );
        assert_eq!(command(Dialect::Rigol, 1, "DUTY").unwrap().1, 100.);
        assert!(command(Dialect::Keysight, 1, "CMEAN").is_none());
    }
}

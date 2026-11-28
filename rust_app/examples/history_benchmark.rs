//! Reproducible large-history navigation benchmark; no hardware or GUI required.
use scoperec::model::Series;
use std::time::Instant;

fn main() {
    let count = 1_000_000;
    let mut series = Vec::new();
    for (key, serial) in [("C1_RMS", false), ("C2_PKPK", false), ("Serial1_T", true)] {
        let mut s = Series::new(key, serial);
        for i in 0..count {
            let value = if i == 500_001 {
                100.
            } else if i == 600_001 {
                f64::NAN
            } else {
                (i as f64 * 0.01).sin()
            };
            s.add(i as f64 * 0.25, value, count);
        }
        series.push(s);
    }
    let start = Instant::now();
    let mut drawn = 0;
    for i in 0..1_000 {
        let lo = i as f64 * 249.;
        for s in &series {
            let points = s.envelope(lo, lo + 120., 1500);
            assert!(points.len() < 500);
            drawn += std::hint::black_box(points.len());
            assert!(s.nearest(lo + 60.).is_some());
        }
    }
    let navigation_ms = start.elapsed().as_secs_f64() * 1000.;
    let start = Instant::now();
    let mut all_points = 0;
    for s in &series {
        let points = s.envelope(0., 250_000., 1500);
        assert!(points.len() < 16_000);
        assert!(points.iter().any(|p| p[1] == 100.));
        assert!(points.iter().any(|p| p[1].is_nan()));
        assert!(points.windows(2).all(|p| p[0][0] <= p[1][0]));
        all_points += points.len();
    }
    let overview_ms = start.elapsed().as_secs_f64() * 1000.;
    println!(
        "{}",
        serde_json::json!({
            "status": "PASS", "device": "synthetic data only", "series": series.len(),
            "stored_points": count * series.len(), "history_hours": count as f64 * 0.25 / 3600.,
            "navigation_windows": 1000, "navigation_total_ms": navigation_ms,
            "navigation_ms_per_window": navigation_ms / 1000.,
            "overview_ms": overview_ms, "overview_points": all_points,
            "navigation_points": drawn, "spike_and_gap_preserved": true,
            "scope": "data preparation; excludes GPU rendering and operating-system input latency"
        })
    );
}

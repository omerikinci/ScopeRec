use scoperec::{
    engine::{self, Command, Engine, EngineEvent},
    model::{Config, Connection, Dialect, PARAMS},
    protocol,
    recording::{Recording, suffix},
    serial::{self, SerialCommand, SerialEvent, SerialSession},
    simulator::{SimConfig, SimServer},
    transport::{self, Instrument},
};
use std::{
    io::{BufRead, BufReader, Write},
    net::TcpListener,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::{Duration, Instant},
};

fn token() -> Arc<AtomicBool> {
    Arc::new(AtomicBool::new(false))
}
fn wait(engine: &Engine, predicate: impl Fn(&EngineEvent) -> bool) -> EngineEvent {
    let until = Instant::now() + Duration::from_secs(10);
    loop {
        let e = engine
            .events
            .recv_timeout(until.saturating_duration_since(Instant::now()))
            .expect("engine event timed out");
        if let EngineEvent::Error(e) = &e {
            panic!("engine error: {e}");
        }
        if predicate(&e) {
            return e;
        }
    }
}

#[test]
fn all_command_sets_talk_to_independent_tcp_simulator() {
    for brand in [
        Dialect::Siglent,
        Dialect::Rigol,
        Dialect::Keysight,
        Dialect::Tektronix,
        Dialect::Generic,
    ] {
        let server = SimServer::start(
            "127.0.0.1:0",
            SimConfig {
                brand,
                ..Default::default()
            },
        )
        .unwrap();
        let mut client =
            transport::TcpInstrument::connect(&server.address.to_string(), token()).unwrap();
        let id = client.text("*IDN?").unwrap();
        assert!(id.contains("RUST-SIM-001"));
        let codes = vec![
            "RMS".to_owned(),
            "MEAN".into(),
            "PKPK".into(),
            "FREQ".into(),
            "DUTY".into(),
        ];
        let result = protocol::measure(&mut client, brand, 2, &codes).unwrap();
        assert!(
            result.iter().all(|v| v.is_finite()),
            "{brand:?}: {result:?}"
        );
        assert!(result[0] > 1.);
        assert!(result[2] > 3.);
        assert!(result[3] > 900.);
        assert_eq!(result[4], 50.);
        drop(client);
    }
}

#[test]
fn tcp_screenshot_and_waveform_are_binary_safe() {
    let server = SimServer::start("127.0.0.1:0", Default::default()).unwrap();
    let mut client =
        transport::TcpInstrument::connect(&server.address.to_string(), token()).unwrap();
    let bmp = client.query("SCDP").unwrap();
    assert_eq!(&bmp[..2], b"BM");
    assert!(image::load_from_memory(&bmp).is_ok());
    let wave = client.query("C1:WF? DAT2").unwrap();
    assert_eq!(wave.len(), 2048);
    assert!(wave.contains(&10));
    assert!(client.text("*IDN?").unwrap().contains("RUST-SIM"));
}

#[test]
fn tcp_read_is_cancelled_without_waiting_for_long_device_delay() {
    let server = SimServer::start(
        "127.0.0.1:0",
        SimConfig {
            delay_ms: 5000,
            ..Default::default()
        },
    )
    .unwrap();
    let cancel = token();
    let mut client =
        transport::TcpInstrument::connect(&server.address.to_string(), cancel.clone()).unwrap();
    let handle = std::thread::spawn(move || {
        let started = Instant::now();
        assert!(client.text("*IDN?").is_err());
        started.elapsed()
    });
    std::thread::sleep(Duration::from_millis(150));
    cancel.store(true, Ordering::SeqCst);
    assert!(handle.join().unwrap() < Duration::from_secs(1));
}

#[test]
fn explicit_query_timeout_is_honored() {
    let server = SimServer::start(
        "127.0.0.1:0",
        SimConfig {
            delay_ms: 5000,
            ..Default::default()
        },
    )
    .unwrap();
    let mut client =
        transport::TcpInstrument::connect(&server.address.to_string(), token()).unwrap();
    client.set_timeout(Duration::from_millis(60));
    let started = Instant::now();
    assert!(client.text("*IDN?").is_err());
    assert!(started.elapsed() < Duration::from_secs(1));
}

#[test]
fn recording_pause_resume_limits_and_markers_share_one_file() {
    let tmp = tempfile::tempdir().unwrap();
    let server = SimServer::start("127.0.0.1:0", Default::default()).unwrap();
    let cfg = Config {
        connection: Connection::Lan,
        address: server.address.to_string(),
        directory: tmp.path().into(),
        record: true,
        channels: [true, true, false, false],
        params: vec!["RMS".into(), "MEAN".into(), "PKPK".into()],
        interval_ms: 40,
        limit: true,
        limit_series: "C1_RMS".into(),
        high: Some(0.5),
        ..Default::default()
    };
    let engine = Engine::new(engine::shared_run());
    engine.send(Command::Connect(cfg.clone()));
    wait(&engine, |e| matches!(e, EngineEvent::Connected(..)));
    engine.send(Command::Start(cfg.clone()));
    let first = wait(&engine, |e| matches!(e, EngineEvent::Started { .. }));
    let stem = if let EngineEvent::Started {
        stem: Some(p),
        resumed: false,
        ..
    } = first
    {
        p
    } else {
        panic!("missing recorder")
    };
    let mut count = 0;
    while count < 8 {
        if let EngineEvent::Sample { values, .. } =
            wait(&engine, |e| matches!(e, EngineEvent::Sample { .. }))
        {
            assert_eq!(values.len(), 6);
            assert!(
                values
                    .iter()
                    .any(|(k, v)| k == "C2_MEAN" && (*v - 0.02).abs() < 1e-9)
            );
            count += 1;
        }
    }
    engine.send(Command::Pause);
    wait(&engine, |e| matches!(e, EngineEvent::Paused));
    engine.send(Command::Mark("test marker".into()));
    std::thread::sleep(Duration::from_millis(80));
    engine.send(Command::Start(cfg));
    let resume = wait(&engine, |e| matches!(e, EngineEvent::Started { .. }));
    assert!(matches!(resume,EngineEvent::Started{stem:Some(ref p),resumed:true,..}if p==&stem));
    wait(&engine, |e| matches!(e, EngineEvent::Sample { .. }));
    engine.send(Command::End);
    let end = wait(&engine, |e| matches!(e, EngineEvent::Ended { .. }));
    let (samples, violations) = if let EngineEvent::Ended {
        samples,
        violations,
    } = end
    {
        (samples, violations)
    } else {
        unreachable!()
    };
    assert!(samples >= 9);
    assert!(violations >= 1);
    let r = Recording::load(&suffix(&stem, ".csv")).unwrap();
    assert_eq!(r.series["C1_RMS"].stats.count, samples);
    assert!(r.events.iter().any(|e| e.text.contains("test marker")));
    assert!(r.events.iter().any(|e| e.kind == "pause"));
    assert!(r.events.iter().any(|e| e.kind == "limit"));
    let times: Vec<_> = r.series["C1_RMS"].points.iter().map(|p| p[0]).collect();
    assert!(times.windows(2).all(|p| p[1] > p[0]));
    engine.send(Command::Shutdown);
    wait(&engine, |e| matches!(e, EngineEvent::Closed));
}

#[test]
fn engine_reconnects_after_simulated_cable_loss() {
    let server = SimServer::start(
        "127.0.0.1:0",
        SimConfig {
            disconnect_after: 5,
            ..Default::default()
        },
    )
    .unwrap();
    let engine = Engine::new(engine::shared_run());
    let c = Config {
        connection: Connection::Lan,
        address: server.address.to_string(),
        interval_ms: 40,
        ..Default::default()
    };
    engine.send(Command::Start(c));
    let mut reconnects = 0;
    let mut samples = 0;
    let until = Instant::now() + Duration::from_secs(8);
    while Instant::now() < until && reconnects < 2 {
        let e = engine.events.recv_timeout(Duration::from_secs(2)).unwrap();
        match e {
            EngineEvent::Connected(..) => reconnects += 1,
            EngineEvent::Sample { .. } => samples += 1,
            EngineEvent::Error(e) => panic!("{e}"),
            _ => {}
        }
    }
    assert!(reconnects >= 2 && samples >= 2);
    engine.send(Command::End);
    wait(&engine, |e| matches!(e, EngineEvent::Ended { .. }));
}

#[test]
fn three_virtual_serial_sessions_record_and_send_independently() {
    let dir = tempfile::tempdir().unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    let (peer_tx, peer_rx) = crossbeam_channel::bounded(1);
    let peer = std::thread::spawn(move || {
        let (mut s, _) = listener.accept().unwrap();
        s.set_read_timeout(Some(Duration::from_secs(4))).unwrap();
        s.write_all(b"SET:1500,ACT:1498,T:+45.3\r\n").unwrap();
        let mut line = String::new();
        BufReader::new(s).read_line(&mut line).unwrap();
        peer_tx.send(line).unwrap();
    });
    let shared = engine::shared_run();
    let (tx, rx) = serial::channel();
    let cfg = scoperec::model::SerialConfig {
        port: format!("tcp://{address}"),
        record: true,
        ..Default::default()
    };
    let s0 = SerialSession::start(0, cfg, shared.clone(), dir.path().into(), tx.clone());
    let s1 = SerialSession::start(
        1,
        Default::default(),
        shared.clone(),
        dir.path().into(),
        tx.clone(),
    );
    let s2 = SerialSession::start(2, Default::default(), shared, dir.path().into(), tx);
    let mut seen = [false; 3];
    let until = Instant::now() + Duration::from_secs(5);
    while !seen.iter().all(|x| *x) && Instant::now() < until {
        if let SerialEvent::Values(i, _, _, values) =
            rx.recv_timeout(Duration::from_secs(1)).unwrap()
        {
            assert!(values.contains_key("T"));
            seen[i] = true;
        }
    }
    assert!(seen.into_iter().all(|x| x));
    s0.send(SerialCommand::Send("HELLO".into()));
    assert_eq!(
        peer_rx.recv_timeout(Duration::from_secs(3)).unwrap(),
        "HELLO\r\n"
    );
    for s in [&s0, &s1, &s2] {
        s.send(SerialCommand::Stop);
    }
    drop((s0, s1, s2));
    peer.join().unwrap();
    let file = std::fs::read_dir(dir.path())
        .unwrap()
        .next()
        .unwrap()
        .unwrap()
        .path();
    assert!(std::fs::read_to_string(file).unwrap().contains("T:+45.3"));
}

#[test]
fn all_seventeen_measurements_and_undefined_values() {
    let mut client = scoperec::simulator::SimClient::new(SimConfig::default(), token());
    let codes = PARAMS.iter().map(|p| p.code.to_owned()).collect::<Vec<_>>();
    assert!(
        protocol::measure(&mut client, Dialect::Siglent, 1, &codes)
            .unwrap()
            .iter()
            .all(|v| v.is_finite())
    );
    let mut bad = scoperec::simulator::SimClient::new(
        SimConfig {
            invalid_every: 1,
            ..Default::default()
        },
        token(),
    );
    assert!(
        protocol::measure(&mut bad, Dialect::Siglent, 1, &codes)
            .unwrap()
            .iter()
            .all(|v| v.is_nan())
    );
}

#[test]
fn engine_shutdown_cancels_blocked_connection() {
    let server = SimServer::start(
        "127.0.0.1:0",
        SimConfig {
            delay_ms: 5000,
            ..Default::default()
        },
    )
    .unwrap();
    let engine = Engine::new(engine::shared_run());
    engine.send(Command::Connect(Config {
        connection: Connection::Lan,
        address: server.address.to_string(),
        ..Default::default()
    }));
    std::thread::sleep(Duration::from_millis(100));
    let start = Instant::now();
    engine.send(Command::Shutdown);
    drop(engine);
    assert!(start.elapsed() < Duration::from_secs(1));
}

use crate::model::{Config, Connection};
use anyhow::{Context, Result, anyhow, bail};
use rusb::{Direction, TransferType};
use serde::Serialize;
use std::{
    collections::VecDeque,
    io::{Read, Write},
    net::{TcpStream, ToSocketAddrs},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::{Duration, Instant},
};
pub type Cancel = Arc<AtomicBool>;
pub fn check_cancel(cancel: &Cancel) -> Result<()> {
    if cancel.load(Ordering::Relaxed) {
        bail!("cancelled");
    }
    Ok(())
}
pub trait Instrument: Send {
    fn set_timeout(&mut self, _timeout: Duration) {}
    fn write(&mut self, command: &str) -> Result<()>;
    fn read(&mut self) -> Result<Vec<u8>>;
    fn clear(&mut self) -> Result<()>;
    fn query(&mut self, command: &str) -> Result<Vec<u8>> {
        self.write(command)?;
        let data = self.read()?;
        decode_block(&data)
    }
    fn text(&mut self, command: &str) -> Result<String> {
        Ok(String::from_utf8_lossy(&self.query(command)?)
            .trim_matches(['\r', '\n', '\0', ' '])
            .to_owned())
    }
}
pub fn open(config: &Config, cancel: Cancel) -> Result<Box<dyn Instrument>> {
    match config.connection {
        Connection::Simulator => Ok(Box::new(crate::simulator::SimClient::new(
            crate::simulator::SimConfig {
                brand: if config.dialect == crate::model::Dialect::Auto {
                    crate::model::Dialect::Siglent
                } else {
                    config.dialect
                },
                ..Default::default()
            },
            cancel,
        ))),
        Connection::Lan => Ok(Box::new(TcpInstrument::connect(&config.address, cancel)?)),
        Connection::Usb => Ok(Box::new(UsbInstrument::connect(&config.usb, cancel)?)),
    }
}
const LIMIT: usize = 64 * 1024 * 1024;
pub fn decode_block(data: &[u8]) -> Result<Vec<u8>> {
    if data.starts_with(b"BM") {
        return Ok(data.to_vec());
    }
    if let Some(i) = data.iter().take(128).position(|&b| b == b'#')
        && let Some(n) = data
            .get(i + 1)
            .filter(|b| b.is_ascii_digit())
            .map(|b| (b - b'0') as usize)
        && n > 0
    {
        let digits = data
            .get(i + 2..i + 2 + n)
            .context("short SCPI block header")?;
        let size = std::str::from_utf8(digits)?.parse::<usize>()?;
        if size > LIMIT {
            bail!("SCPI block exceeds 64 MiB");
        }
        return Ok(data
            .get(i + 2 + n..i + 2 + n + size)
            .context("short SCPI block payload")?
            .to_vec());
    }
    Ok(data.to_vec())
}

pub struct TcpInstrument {
    stream: TcpStream,
    buffer: VecDeque<u8>,
    cancel: Cancel,
    timeout: Duration,
}
impl TcpInstrument {
    pub fn connect(address: &str, cancel: Cancel) -> Result<Self> {
        check_cancel(&cancel)?;
        let address = address.trim();
        let endpoint = if address.starts_with('[')
            || address
                .rsplit_once(':')
                .is_some_and(|(_, p)| p.parse::<u16>().is_ok())
        {
            address.to_owned()
        } else {
            format!("{address}:5025")
        };
        let mut last = None;
        for addr in endpoint.to_socket_addrs()? {
            check_cancel(&cancel)?;
            match TcpStream::connect_timeout(&addr, Duration::from_secs(2)) {
                Ok(stream) => {
                    stream.set_nodelay(true)?;
                    stream.set_read_timeout(Some(Duration::from_millis(100)))?;
                    stream.set_write_timeout(Some(Duration::from_millis(500)))?;
                    return Ok(Self {
                        stream,
                        buffer: VecDeque::new(),
                        cancel,
                        timeout: Duration::from_secs(10),
                    });
                }
                Err(e) => last = Some(e),
            }
        }
        Err(anyhow!(
            "LAN connection failed: {}",
            last.map_or_else(|| "no address".into(), |e| e.to_string())
        ))
    }
    fn byte(&mut self, deadline: Instant) -> Result<u8> {
        loop {
            check_cancel(&self.cancel)?;
            if let Some(b) = self.buffer.pop_front() {
                return Ok(b);
            }
            if Instant::now() > deadline {
                bail!("SCPI response timed out");
            }
            let mut bytes = [0u8; 16384];
            match self.stream.read(&mut bytes) {
                Ok(0) => bail!("SCPI connection closed"),
                Ok(n) => self.buffer.extend(&bytes[..n]),
                Err(e)
                    if matches!(
                        e.kind(),
                        std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut
                    ) => {}
                Err(e) => return Err(e.into()),
            }
        }
    }
    fn exact(&mut self, n: usize, deadline: Instant) -> Result<Vec<u8>> {
        if n > LIMIT {
            bail!("response exceeds 64 MiB");
        }
        let mut out = Vec::with_capacity(n);
        for _ in 0..n {
            out.push(self.byte(deadline)?);
        }
        Ok(out)
    }
}
impl Instrument for TcpInstrument {
    fn set_timeout(&mut self, timeout: Duration) {
        self.timeout = timeout;
    }
    fn write(&mut self, command: &str) -> Result<()> {
        check_cancel(&self.cancel)?;
        self.stream.write_all(command.trim_end().as_bytes())?;
        self.stream.write_all(b"\n")?;
        Ok(())
    }
    fn read(&mut self) -> Result<Vec<u8>> {
        let deadline = Instant::now() + self.timeout;
        let mut out = Vec::new();
        loop {
            let b = self.byte(deadline)?;
            if out.is_empty() && matches!(b, b'\r' | b'\n') {
                continue;
            }
            out.push(b);
            if out == b"BM" {
                let header = self.exact(4, deadline)?;
                out.extend(header);
                let size = u32::from_le_bytes(out[2..6].try_into()?) as usize;
                if !(54..=LIMIT).contains(&size) {
                    bail!("invalid BMP size");
                }
                out.extend(self.exact(size - 6, deadline)?);
                return Ok(out);
            }
            if b == b'#' && out.len() < 128 {
                let n = self.byte(deadline)?;
                out.push(n);
                if (b'1'..=b'9').contains(&n) {
                    let digits = self.exact((n - b'0') as usize, deadline)?;
                    let size = std::str::from_utf8(&digits)?.parse::<usize>()?;
                    out.extend(digits);
                    out.extend(self.exact(size, deadline)?);
                    return Ok(out);
                }
            }
            if b == b'\n' {
                return Ok(out);
            }
            if out.len() > LIMIT {
                bail!("oversized response");
            }
        }
    }
    fn clear(&mut self) -> Result<()> {
        self.buffer.clear();
        self.stream.set_nonblocking(true)?;
        let mut b = [0u8; 65536];
        let until = Instant::now() + Duration::from_millis(200);
        while Instant::now() < until {
            match self.stream.read(&mut b) {
                Ok(0) | Err(_) => break,
                Ok(_) => {}
            }
        }
        self.stream.set_nonblocking(false)?;
        Ok(())
    }
}

#[derive(Clone, Debug, Serialize)]
pub struct UsbDevice {
    pub selector: String,
    pub label: String,
}
pub fn usb_devices() -> Result<Vec<UsbDevice>> {
    let mut out = vec![];
    for dev in rusb::devices()?.iter() {
        let d = dev.device_descriptor()?;
        let cfg = dev
            .active_config_descriptor()
            .or_else(|_| dev.config_descriptor(0));
        let Ok(cfg) = cfg else { continue };
        if !cfg.interfaces().any(|i| {
            i.descriptors()
                .any(|a| a.class_code() == 0xfe && a.sub_class_code() == 3)
        }) {
            continue;
        }
        let selector = format!(
            "{:04x}:{:04x}@{}:{}",
            d.vendor_id(),
            d.product_id(),
            dev.bus_number(),
            dev.address()
        );
        let extra = dev
            .open()
            .ok()
            .map(|h| {
                format!(
                    "{} {} {}",
                    h.read_manufacturer_string_ascii(&d).unwrap_or_default(),
                    h.read_product_string_ascii(&d).unwrap_or_default(),
                    h.read_serial_number_string_ascii(&d).unwrap_or_default()
                )
            })
            .unwrap_or_default();
        out.push(UsbDevice {
            label: format!("{selector} {}", extra.trim()),
            selector,
        });
    }
    Ok(out)
}
pub fn write_packet(command: &str, tag: u8) -> Vec<u8> {
    let payload = format!("{}\n", command.trim_end());
    let mut packet = vec![0; (12 + payload.len() + 3) & !3];
    packet[0] = 1;
    packet[1] = tag;
    packet[2] = !tag;
    packet[4..8].copy_from_slice(&(payload.len() as u32).to_le_bytes());
    packet[8] = 1;
    packet[12..12 + payload.len()].copy_from_slice(payload.as_bytes());
    packet
}
pub fn read_request(tag: u8) -> [u8; 12] {
    let mut p = [0; 12];
    p[0] = 2;
    p[1] = tag;
    p[2] = !tag;
    p[4..8].copy_from_slice(&65524u32.to_le_bytes());
    p
}
pub struct UsbInstrument {
    handle: rusb::DeviceHandle<rusb::GlobalContext>,
    interface: u8,
    input: u8,
    output: u8,
    tag: u8,
    pending: bool,
    local: bool,
    cancel: Cancel,
    timeout: Duration,
}
impl UsbInstrument {
    pub fn connect(selector: &str, cancel: Cancel) -> Result<Self> {
        check_cancel(&cancel)?;
        for dev in rusb::devices()?.iter() {
            let d = dev.device_descriptor()?;
            let key = format!(
                "{:04x}:{:04x}@{}:{}",
                d.vendor_id(),
                d.product_id(),
                dev.bus_number(),
                dev.address()
            );
            if !selector.is_empty()
                && !key.to_lowercase().contains(
                    &selector
                        .split_whitespace()
                        .next()
                        .unwrap_or("")
                        .to_lowercase(),
                )
            {
                continue;
            }
            let Ok(cfg) = dev
                .active_config_descriptor()
                .or_else(|_| dev.config_descriptor(0))
            else {
                continue;
            };
            for intf in cfg.interfaces() {
                for alt in intf.descriptors() {
                    if alt.class_code() != 0xfe || alt.sub_class_code() != 3 {
                        continue;
                    }
                    let input = alt
                        .endpoint_descriptors()
                        .find(|e| {
                            e.transfer_type() == TransferType::Bulk
                                && e.direction() == Direction::In
                        })
                        .map(|e| e.address());
                    let output = alt
                        .endpoint_descriptors()
                        .find(|e| {
                            e.transfer_type() == TransferType::Bulk
                                && e.direction() == Direction::Out
                        })
                        .map(|e| e.address());
                    let (Some(input), Some(output)) = (input, output) else {
                        continue;
                    };
                    let handle = dev
                        .open()
                        .context("USB device cannot be opened; check driver/permissions")?;
                    let _ = handle.set_auto_detach_kernel_driver(true);
                    if dev.active_config_descriptor().is_err() {
                        handle.set_active_configuration(cfg.number())?;
                    }
                    handle.claim_interface(alt.interface_number()).context(
                        "USB interface is busy; disconnect the other ScopeRec application",
                    )?;
                    if alt.setting_number() != 0 {
                        handle
                            .set_alternate_setting(alt.interface_number(), alt.setting_number())?;
                    }
                    let mut caps = [0u8; 24];
                    let local = handle
                        .read_control(
                            0xa1,
                            7,
                            0,
                            alt.interface_number() as u16,
                            &mut caps,
                            Duration::from_millis(250),
                        )
                        .is_ok_and(|n| n >= 15 && caps[0] == 1 && caps[14] & 2 != 0);
                    return Ok(Self {
                        handle,
                        interface: alt.interface_number(),
                        input,
                        output,
                        tag: 1,
                        pending: false,
                        local,
                        cancel,
                        timeout: Duration::from_secs(10),
                    });
                }
            }
        }
        bail!("No matching USB Test & Measurement device")
    }
    fn next(&mut self) -> u8 {
        self.tag = if self.tag == 255 { 1 } else { self.tag + 1 };
        self.tag
    }
    fn bulk(&self, buffer: &mut [u8], deadline: Instant) -> Result<usize> {
        loop {
            check_cancel(&self.cancel)?;
            if Instant::now() > deadline {
                bail!("USB response timed out");
            }
            match self
                .handle
                .read_bulk(self.input, buffer, Duration::from_millis(100))
            {
                Ok(n) => return Ok(n),
                Err(rusb::Error::Timeout) => {}
                Err(e) => return Err(e.into()),
            }
        }
    }
}
impl Instrument for UsbInstrument {
    fn set_timeout(&mut self, timeout: Duration) {
        self.timeout = timeout;
    }
    fn write(&mut self, command: &str) -> Result<()> {
        check_cancel(&self.cancel)?;
        let tag = self.next();
        let p = write_packet(command, tag);
        let n = self
            .handle
            .write_bulk(self.output, &p, Duration::from_millis(500))?;
        if n != p.len() {
            bail!("short USB write");
        }
        Ok(())
    }
    fn read(&mut self) -> Result<Vec<u8>> {
        let mut out = vec![];
        let deadline = Instant::now() + self.timeout;
        let mut buffer = vec![0u8; 65536];
        for _ in 0..2048 {
            check_cancel(&self.cancel)?;
            let tag = self.next();
            self.pending = true;
            self.handle
                .write_bulk(self.output, &read_request(tag), Duration::from_millis(500))?;
            let n = self.bulk(&mut buffer, deadline)?;
            if n < 12 || buffer[0] != 2 || buffer[1] != tag || buffer[2] != !tag {
                bail!("USBTMC response tag/header mismatch");
            }
            let size = u32::from_le_bytes(buffer[4..8].try_into()?) as usize;
            if size > 65524 || out.len() + size > LIMIT {
                bail!("invalid USBTMC payload size");
            }
            let eom = buffer[8] & 1 != 0;
            let mut got = (n - 12).min(size);
            out.extend_from_slice(&buffer[12..12 + got]);
            while got < size {
                let n = self.bulk(&mut buffer, deadline)?;
                if n == 0 {
                    bail!("short USBTMC response");
                }
                let take = n.min(size - got);
                out.extend_from_slice(&buffer[..take]);
                got += take;
            }
            if eom {
                self.pending = false;
                return Ok(out);
            }
        }
        bail!("USBTMC response is too long")
    }
    fn clear(&mut self) -> Result<()> {
        let mut b = vec![0u8; 65536];
        for _ in 0..8 {
            if self
                .handle
                .read_bulk(self.input, &mut b, Duration::from_millis(20))
                .is_err()
            {
                break;
            }
        }
        let mut status = [0u8; 2];
        if self
            .handle
            .read_control(
                0xa1,
                5,
                0,
                self.interface as u16,
                &mut status[..1],
                Duration::from_millis(100),
            )
            .is_ok()
        {
            for _ in 0..8 {
                match self.handle.read_control(
                    0xa1,
                    6,
                    0,
                    self.interface as u16,
                    &mut status,
                    Duration::from_millis(100),
                ) {
                    Ok(_) if status[0] == 2 => std::thread::sleep(Duration::from_millis(10)),
                    _ => break,
                }
            }
        }
        let _ = self.handle.clear_halt(self.input);
        let _ = self.handle.clear_halt(self.output);
        self.pending = false;
        Ok(())
    }
}
impl Drop for UsbInstrument {
    fn drop(&mut self) {
        if self.pending {
            let _ = self.clear();
        }
        if self.local {
            let mut b = [0u8; 1];
            for request in [161, 160] {
                let _ = self.handle.read_control(
                    0xa1,
                    request,
                    0,
                    self.interface as u16,
                    &mut b,
                    Duration::from_millis(100),
                );
            }
        }
        let _ = self.handle.release_interface(self.interface);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn tmc_headers() {
        let p = write_packet("*IDN?", 255);
        assert_eq!(&p[..4], &[1, 255, 0, 0]);
        assert_eq!(p.len() % 4, 0);
        assert_eq!(&p[12..18], b"*IDN?\n");
        let r = read_request(1);
        assert_eq!(r[2], 254);
        assert_eq!(u32::from_le_bytes(r[4..8].try_into().unwrap()), 65524);
    }
    #[test]
    fn binary_blocks() {
        assert_eq!(
            decode_block(b"C1:WF DAT2,#14\x00\n\xff\x00\n").unwrap(),
            [0, 10, 255, 0]
        );
        assert!(decode_block(b"#210ab").is_err());
    }
}

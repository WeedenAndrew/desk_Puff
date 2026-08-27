use std::collections::HashMap;
use std::env;
use std::error::Error;
use std::io;
use std::time::Duration;

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64;
use btleplug::api::{
    Central, CharPropFlags, Characteristic, Manager as _, Peripheral as _, ScanFilter, WriteType,
};
use btleplug::platform::{Adapter, Manager, Peripheral};
use futures_util::StreamExt as _;
use serde::{Deserialize, Serialize};
use tokio::io::{AsyncReadExt as _, AsyncWriteExt as _};
use uuid::{Uuid, uuid};

const SERVICE_UUID: Uuid = uuid!("e276967f-ea8a-478a-a92e-d78f5dd15dd5");
const VERSION_UUID: Uuid = uuid!("05434bca-cc7f-4ef6-bbb3-b1c520b9800c");
const COMMAND_UUID: Uuid = uuid!("60133d5c-5727-4f2c-9697-d842c5292a3c");
const REPLY_UUID: Uuid = uuid!("8dc5ec05-8f7d-45ad-99db-3fbde65dbd9c");
const MAXIMUM_REQUEST_BYTES: usize = 4096;
const MAXIMUM_RESPONSE_BYTES: usize = 64 * 1024;
const MAXIMUM_FRAME_BYTES: usize = 515;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Request {
    id: u64,
    operation: String,
    duration_milliseconds: Option<u64>,
    candidate_id: Option<String>,
    frame_base64: Option<String>,
    expected_sequence: Option<u16>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Response {
    id: u64,
    success: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    advertised_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    frame_base64: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    candidates: Option<Vec<Candidate>>,
}

impl Response {
    fn ok(id: u64) -> Self {
        Self {
            id,
            success: true,
            error: None,
            advertised_name: None,
            frame_base64: None,
            candidates: None,
        }
    }

    fn failure(id: u64, error: &dyn Error) -> Self {
        let mut message = error.to_string().replace(['\r', '\n'], " ");
        message.truncate(240);
        Self {
            id,
            success: false,
            error: Some(message),
            advertised_name: None,
            frame_base64: None,
            candidates: None,
        }
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Candidate {
    id: String,
    name: String,
    signal_strength: i16,
}

struct Connection {
    peripheral: Peripheral,
    version: Characteristic,
    command: Characteristic,
    reply: Characteristic,
    advertised_name: String,
}

struct BleState {
    adapter: Adapter,
    candidates: HashMap<String, Peripheral>,
    connection: Option<Connection>,
}

impl BleState {
    async fn new() -> Result<Self, Box<dyn Error + Send + Sync>> {
        let manager = Manager::new().await?;
        let adapter = manager
            .adapters()
            .await?
            .into_iter()
            .next()
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "No Bluetooth adapter is available."))?;
        Ok(Self {
            adapter,
            candidates: HashMap::new(),
            connection: None,
        })
    }

    async fn scan(&mut self, duration: Duration) -> Result<Vec<Candidate>, Box<dyn Error + Send + Sync>> {
        if duration.is_zero() || duration > Duration::from_secs(30) {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "Scan duration is outside the allowed range.").into());
        }

        self.adapter.start_scan(ScanFilter::default()).await?;
        tokio::time::sleep(duration).await;
        self.adapter.stop_scan().await?;
        self.candidates.clear();

        let mut found = Vec::new();
        for peripheral in self.adapter.peripherals().await? {
            let Some(properties) = peripheral.properties().await? else {
                continue;
            };
            let name = properties.local_name.unwrap_or_default();
            if !is_puff_candidate(&name, &properties.services) {
                continue;
            }

            let id = peripheral.id().to_string();
            let signal_strength = properties.rssi.unwrap_or(i16::MIN);
            self.candidates.insert(id.clone(), peripheral);
            found.push(Candidate {
                id,
                name,
                signal_strength,
            });
        }

        found.sort_by(|left, right| right.signal_strength.cmp(&left.signal_strength));
        Ok(found)
    }

    async fn connect(&mut self, candidate_id: &str) -> Result<String, Box<dyn Error + Send + Sync>> {
        self.disconnect().await?;
        let peripheral = self
            .candidates
            .get(candidate_id)
            .cloned()
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "The selected e-rig is no longer in the scan results."))?;

        tokio::time::timeout(Duration::from_secs(12), peripheral.connect())
            .await
            .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "Bluetooth connection timed out."))??;
        if let Err(error) = tokio::time::timeout(Duration::from_secs(12), peripheral.discover_services())
            .await
            .map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "Bluetooth service discovery timed out."))?
        {
            let _ = peripheral.disconnect().await;
            return Err(error.into());
        }

        let characteristics = peripheral.characteristics();
        for characteristic in characteristics
            .iter()
            .filter(|characteristic| characteristic.service_uuid == SERVICE_UUID)
        {
            eprintln!(
                "Lorax characteristic: uuid={} properties={:?}",
                characteristic.uuid, characteristic.properties
            );
        }
        let version = required_characteristic(&characteristics, VERSION_UUID, CharPropFlags::READ)?;
        let command = required_characteristic(
            &characteristics,
            COMMAND_UUID,
            CharPropFlags::WRITE_WITHOUT_RESPONSE,
        )?;
        let reply = required_characteristic(&characteristics, REPLY_UUID, CharPropFlags::NOTIFY)?;
        if !peripheral.services().iter().any(|service| service.uuid == SERVICE_UUID) {
            let _ = peripheral.disconnect().await;
            return Err(io::Error::new(io::ErrorKind::NotFound, "The device does not expose the Lorax service.").into());
        }

        peripheral.subscribe(&reply).await?;
        let advertised_name = peripheral
            .properties()
            .await?
            .and_then(|properties| properties.local_name)
            .unwrap_or_else(|| "PUFFCO E-RIG".to_owned());
        self.connection = Some(Connection {
            peripheral,
            version,
            command,
            reply,
            advertised_name: advertised_name.clone(),
        });
        Ok(advertised_name)
    }

    async fn disconnect(&mut self) -> Result<(), Box<dyn Error + Send + Sync>> {
        if let Some(connection) = self.connection.take() {
            let _ = connection.peripheral.unsubscribe(&connection.reply).await;
            if connection.peripheral.is_connected().await.unwrap_or(false) {
                connection.peripheral.disconnect().await?;
            }
        }
        Ok(())
    }

    async fn trigger_bonding(&self) -> Result<(), Box<dyn Error + Send + Sync>> {
        let connection = self.connection.as_ref().ok_or_else(not_connected)?;
        let version = connection.peripheral.read(&connection.version).await?;
        let version_hex = version
            .iter()
            .map(|byte| format!("{byte:02X}"))
            .collect::<Vec<_>>()
            .join(" ");
        let version_ascii = version
            .iter()
            .map(|byte| {
                if byte.is_ascii_graphic() || *byte == b' ' {
                    char::from(*byte)
                } else {
                    '.'
                }
            })
            .collect::<String>();
        eprintln!(
            "Lorax version: length={} hex={version_hex} ascii=\"{version_ascii}\"",
            version.len()
        );
        Ok(())
    }

    async fn run_command(
        &self,
        frame: &[u8],
        expected_sequence: u16,
    ) -> Result<Vec<u8>, Box<dyn Error + Send + Sync>> {
        validate_frame(frame, expected_sequence)?;
        let connection = self.connection.as_ref().ok_or_else(not_connected)?;
        let mut notifications = connection.peripheral.notifications().await?;
        // Diagnostic trial only, not a transport decision: test whether the device replies to acknowledged writes.
        connection
            .peripheral
            .write(&connection.command, frame, WriteType::WithResponse)
            .await?;
        let frame_sequence = u16::from_le_bytes([frame[0], frame[1]]);
        let frame_hex = frame
            .iter()
            .map(|byte| format!("{byte:02X}"))
            .collect::<Vec<_>>()
            .join(" ");
        eprintln!(
            "Lorax write: sequence={frame_sequence} opcode=0x{:02X} length={} hex={frame_hex}",
            frame[2],
            frame.len()
        );

        tokio::time::timeout(Duration::from_secs(3), async {
            while let Some(notification) = notifications.next().await {
                let notification_hex = notification
                    .value
                    .iter()
                    .map(|byte| format!("{byte:02X}"))
                    .collect::<Vec<_>>()
                    .join(" ");
                eprintln!(
                    "Lorax notification: uuid={} length={} hex={notification_hex}",
                    notification.uuid,
                    notification.value.len()
                );
                if notification.uuid != REPLY_UUID || notification.value.len() < 3 {
                    continue;
                }
                let sequence = u16::from_le_bytes([notification.value[0], notification.value[1]]);
                if sequence == expected_sequence {
                    return Ok(notification.value);
                }
            }
            eprintln!("Lorax notification window ended: stream closed");
            Err(io::Error::new(io::ErrorKind::UnexpectedEof, "Bluetooth notifications ended before the Lorax reply."))
        })
        .await
        .map_err(|_| {
            eprintln!("Lorax notification window ended: timeout");
            io::Error::new(io::ErrorKind::TimedOut, "Lorax reply timed out.")
        })?
        .map_err(Into::into)
    }
}

fn required_characteristic(
    characteristics: &std::collections::BTreeSet<Characteristic>,
    uuid: Uuid,
    required_property: CharPropFlags,
) -> Result<Characteristic, Box<dyn Error + Send + Sync>> {
    let characteristic = characteristics
        .iter()
        .find(|characteristic| characteristic.uuid == uuid)
        .cloned()
        .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, format!("Required characteristic {uuid} is missing.")))?;
    if !characteristic.properties.contains(required_property) {
        return Err(io::Error::new(io::ErrorKind::PermissionDenied, format!("Characteristic {uuid} has unsafe properties.")).into());
    }
    Ok(characteristic)
}

fn is_puff_candidate(name: &str, services: &[Uuid]) -> bool {
    let uppercase = name.to_ascii_uppercase();
    services.contains(&SERVICE_UUID)
        || uppercase.contains("PUFFCO")
        || uppercase.contains("PEAK")
        || uppercase.contains("PROXY")
}

fn validate_frame(frame: &[u8], expected_sequence: u16) -> Result<(), io::Error> {
    if frame.len() < 3 || frame.len() > MAXIMUM_FRAME_BYTES {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "Lorax frame length is invalid."));
    }
    let sequence = u16::from_le_bytes([frame[0], frame[1]]);
    if sequence != expected_sequence {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "Lorax frame sequence is invalid."));
    }
    match frame[2] {
        0x00..=0x02 | 0x10 => Ok(()),
        0x11 => Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "Real-device writes are disabled until hardware validation is complete.",
        )),
        _ => Err(io::Error::new(io::ErrorKind::InvalidInput, "Lorax opcode is not allowlisted.")),
    }
}

fn not_connected() -> io::Error {
    io::Error::new(io::ErrorKind::NotConnected, "The Bluetooth helper is not connected.")
}

async fn read_bounded_line() -> Result<Option<Vec<u8>>, io::Error> {
    let mut stdin = tokio::io::stdin();
    let mut line = Vec::with_capacity(512);
    let mut byte = [0_u8; 1];
    loop {
        let read = stdin.read(&mut byte).await?;
        if read == 0 {
            return if line.is_empty() { Ok(None) } else { Ok(Some(line)) };
        }
        if byte[0] == b'\n' {
            if line.last() == Some(&b'\r') {
                line.pop();
            }
            return Ok(Some(line));
        }
        if line.len() >= MAXIMUM_REQUEST_BYTES {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "Bluetooth helper request exceeded its size limit."));
        }
        line.push(byte[0]);
    }
}

async fn write_response(response: &Response) -> Result<(), Box<dyn Error + Send + Sync>> {
    let mut payload = serde_json::to_vec(response)?;
    if payload.len() > MAXIMUM_RESPONSE_BYTES {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "Bluetooth helper response exceeded its size limit.").into());
    }
    payload.push(b'\n');
    let mut stdout = tokio::io::stdout();
    stdout.write_all(&payload).await?;
    stdout.flush().await?;
    Ok(())
}

async fn execute(state: &mut BleState, request: &Request) -> Result<Response, Box<dyn Error + Send + Sync>> {
    let mut response = Response::ok(request.id);
    match request.operation.as_str() {
        "scan" => {
            let milliseconds = request.duration_milliseconds.ok_or_else(|| {
                io::Error::new(io::ErrorKind::InvalidInput, "Scan duration is required.")
            })?;
            response.candidates = Some(state.scan(Duration::from_millis(milliseconds)).await?);
        }
        "connect" => {
            let candidate_id = request.candidate_id.as_deref().ok_or_else(|| {
                io::Error::new(io::ErrorKind::InvalidInput, "Candidate identifier is required.")
            })?;
            response.advertised_name = Some(state.connect(candidate_id).await?);
        }
        "disconnect" => state.disconnect().await?,
        "triggerBonding" => state.trigger_bonding().await?,
        "runCommand" => {
            let frame = BASE64.decode(request.frame_base64.as_deref().ok_or_else(|| {
                io::Error::new(io::ErrorKind::InvalidInput, "Lorax frame is required.")
            })?)?;
            let expected_sequence = request.expected_sequence.ok_or_else(|| {
                io::Error::new(io::ErrorKind::InvalidInput, "Lorax sequence is required.")
            })?;
            response.frame_base64 = Some(BASE64.encode(state.run_command(&frame, expected_sequence).await?));
        }
        "shutdown" => state.disconnect().await?,
        _ => return Err(io::Error::new(io::ErrorKind::InvalidInput, "Bluetooth helper operation is not allowlisted.").into()),
    }
    Ok(response)
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn Error + Send + Sync>> {
    if env::args().collect::<Vec<_>>().as_slice().get(1).map(String::as_str) != Some("--stdio") {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "desk-puff-ble is an internal stdio helper.").into());
    }

    let mut state = BleState::new().await?;
    while let Some(line) = read_bounded_line().await? {
        let request: Request = match serde_json::from_slice(&line) {
            Ok(request) => request,
            Err(error) => {
                write_response(&Response::failure(0, &error)).await?;
                continue;
            }
        };
        let shutdown = request.operation == "shutdown";
        let response = match execute(&mut state, &request).await {
            Ok(response) => response,
            Err(error) => Response::failure(request.id, error.as_ref()),
        };
        write_response(&response).await?;
        if shutdown {
            break;
        }
    }
    let _ = state.disconnect().await;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn candidate_filter_accepts_service_or_known_name() {
        assert!(is_puff_candidate("anything", &[SERVICE_UUID]));
        assert!(is_puff_candidate("Puffco Peak", &[]));
        assert!(!is_puff_candidate("Headphones", &[]));
    }

    #[test]
    fn frame_validation_allows_reads_and_rejects_writes() {
        assert!(validate_frame(&[7, 0, 0x10], 7).is_ok());
        assert_eq!(
            validate_frame(&[7, 0, 0x11, 0], 7)
                .expect_err("writes must stay disabled")
                .kind(),
            io::ErrorKind::PermissionDenied
        );
    }

    #[test]
    fn frame_validation_rejects_sequence_and_opcode_mismatches() {
        assert!(validate_frame(&[1, 0, 0x10], 2).is_err());
        assert!(validate_frame(&[2, 0, 0xff], 2).is_err());
        assert!(validate_frame(&[2, 0], 2).is_err());
    }
}

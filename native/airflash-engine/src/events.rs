//! Receiver → sender commands carried on the AirPlay event channel.
//!
//! AirFlash acknowledged every event with `200 OK` and then dropped the body, so
//! pressing pause on a HomePod did nothing on the PC. The body is a binary plist
//! whose command key varies by receiver generation (`mrCommand` for AirPlay 2,
//! `command`/`cmd` for older and third-party devices), so the parser is
//! deliberately permissive: it looks for a known key, accepts either the string
//! form or the MRP numeric form, and reports what it could not understand instead
//! of guessing.

use plist::Value;
use std::collections::VecDeque;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RemoteCommand {
    Play,
    Pause,
    TogglePlayPause,
    Stop,
    NextTrack,
    PreviousTrack,
    Mute,
    VolumeUp,
    VolumeDown,
    ShuffleNext,
    RepeatNext,
}

impl RemoteCommand {
    pub fn name(self) -> &'static str {
        match self {
            Self::Play => "play",
            Self::Pause => "pause",
            Self::TogglePlayPause => "toggle",
            Self::Stop => "stop",
            Self::NextTrack => "next",
            Self::PreviousTrack => "previous",
            Self::Mute => "mute",
            Self::VolumeUp => "volume_up",
            Self::VolumeDown => "volume_down",
            Self::ShuffleNext => "shuffle_next",
            Self::RepeatNext => "repeat_next",
        }
    }
    /// MediaRemote numeric command, the encoding Apple uses on the wire.
    pub fn from_mrp(code: u64) -> Option<Self> {
        Some(match code {
            0 => Self::Play,
            1 => Self::Pause,
            2 => Self::TogglePlayPause,
            3 => Self::Stop,
            4 => Self::NextTrack,
            5 => Self::PreviousTrack,
            6 => Self::ShuffleNext,
            7 => Self::RepeatNext,
            29 => Self::Mute,
            30 => Self::VolumeUp,
            31 => Self::VolumeDown,
            _ => return None,
        })
    }
    pub fn from_name(name: &str) -> Option<Self> {
        Some(match name.trim().to_ascii_lowercase().as_str() {
            "play" | "playpause" | "play_pause" => Self::Play,
            "pause" => Self::Pause,
            "toggle" | "toggleplaypause" | "toggle_play_pause" => Self::TogglePlayPause,
            "stop" => Self::Stop,
            "next" | "nexttrack" | "next_track" => Self::NextTrack,
            "previous" | "previoustrack" | "previous_track" | "prev" => Self::PreviousTrack,
            "mute" => Self::Mute,
            "volumeup" | "volume_up" | "volume-up" => Self::VolumeUp,
            "volumedown" | "volume_down" | "volume-down" => Self::VolumeDown,
            "shuffle" | "shufflenext" => Self::ShuffleNext,
            "repeat" | "repeatnext" => Self::RepeatNext,
            _ => return None,
        })
    }
}

/// What one event-channel message asked for.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct ParsedEvent {
    pub commands: Vec<RemoteCommand>,
    /// Absolute volume request in the receiver's own unit (0..1 when normalised).
    pub volume: Option<f64>,
    /// Key that supplied the command, for diagnostics.
    pub source: Option<&'static str>,
}

/// Parse an event body. Unparseable payloads return an empty result rather than an
/// error: the channel must keep running even when a device sends something odd.
pub fn parse(body: &[u8]) -> ParsedEvent {
    if body.is_empty() {
        return ParsedEvent::default();
    }
    let Ok(value) = Value::from_reader(std::io::Cursor::new(body)) else {
        return ParsedEvent::default();
    };
    parse_value(&value)
}

fn parse_value(value: &Value) -> ParsedEvent {
    let Some(dict) = value.as_dictionary() else {
        return ParsedEvent::default();
    };
    let mut parsed = ParsedEvent::default();
    for key in ["mrCommand", "command", "cmd", "action"] {
        let Some(found) = dict.get(key) else {
            continue;
        };
        let commands = commands_of(found);
        if !commands.is_empty() {
            parsed.commands = commands;
            parsed.source = Some(match key {
                "mrCommand" => "mrCommand",
                "command" => "command",
                "cmd" => "cmd",
                _ => "action",
            });
            break;
        }
    }
    if let Some(volume) = dict.get("volume").and_then(normalised_volume) {
        parsed.volume = Some(volume);
    }
    parsed
}

fn commands_of(value: &Value) -> Vec<RemoteCommand> {
    if let Some(name) = value.as_string() {
        return RemoteCommand::from_name(name).into_iter().collect();
    }
    if let Some(code) = value.as_unsigned_integer() {
        return RemoteCommand::from_mrp(code).into_iter().collect();
    }
    if let Some(code) = value.as_signed_integer() {
        return u64::try_from(code)
            .ok()
            .and_then(RemoteCommand::from_mrp)
            .into_iter()
            .collect();
    }
    // Some receivers send a list of commands per message.
    if let Some(list) = value.as_array() {
        let mut commands = Vec::new();
        for item in list {
            commands.extend(commands_of(item));
        }
        return commands;
    }
    Vec::new()
}

fn normalised_volume(value: &Value) -> Option<f64> {
    let raw = value
        .as_real()
        .or_else(|| value.as_unsigned_integer().map(|v| v as f64))
        .or_else(|| value.as_signed_integer().map(|v| v as f64))?;
    if !raw.is_finite() {
        return None;
    }
    Some(if (0.0..=1.0).contains(&raw) {
        raw
    } else if (-144.0..=0.0).contains(&raw) {
        // dBFS form used by some receivers.
        10f64.powf(raw / 20.0)
    } else if (0.0..=100.0).contains(&raw) {
        raw / 100.0
    } else {
        raw.clamp(0.0, 1.0)
    })
}

/// Bounded queue of commands awaiting the session loop.
#[derive(Default)]
pub struct Inbox {
    queue: VecDeque<ParsedEvent>,
    dropped: u64,
}
impl Inbox {
    const LIMIT: usize = 32;
    pub fn push(&mut self, event: ParsedEvent) {
        if self.queue.len() == Self::LIMIT {
            self.queue.pop_front();
            self.dropped += 1;
        }
        self.queue.push_back(event);
    }
    pub fn drain(&mut self) -> Vec<ParsedEvent> {
        self.queue.drain(..).collect()
    }
    pub fn dropped(&self) -> u64 {
        self.dropped
    }
    pub fn len(&self) -> usize {
        self.queue.len()
    }
    pub fn is_empty(&self) -> bool {
        self.queue.is_empty()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use plist::Dictionary;

    fn binary(dict: Dictionary) -> Vec<u8> {
        let mut bytes = Vec::new();
        Value::Dictionary(dict)
            .to_writer_binary(&mut bytes)
            .unwrap();
        bytes
    }

    #[test]
    fn string_and_numeric_commands_agree() {
        for (text, code, expected) in [
            ("play", 0u64, RemoteCommand::Play),
            ("pause", 1, RemoteCommand::Pause),
            ("toggle", 2, RemoteCommand::TogglePlayPause),
            ("next", 4, RemoteCommand::NextTrack),
            ("previous", 5, RemoteCommand::PreviousTrack),
            ("mute", 29, RemoteCommand::Mute),
        ] {
            let mut dict = Dictionary::new();
            dict.insert("mrCommand".into(), Value::String(text.into()));
            assert_eq!(parse(&binary(dict)).commands, vec![expected]);
            let mut dict = Dictionary::new();
            dict.insert("mrCommand".into(), Value::Integer(code.into()));
            assert_eq!(parse(&binary(dict)).commands, vec![expected]);
        }
    }

    #[test]
    fn older_key_names_are_accepted() {
        for key in ["command", "cmd", "action"] {
            let mut dict = Dictionary::new();
            dict.insert(key.into(), Value::String("playpause".into()));
            let parsed = parse(&binary(dict));
            assert_eq!(parsed.commands, vec![RemoteCommand::Play]);
            assert_eq!(parsed.source, Some(match key {
                "command" => "command",
                "cmd" => "cmd",
                _ => "action",
            }));
        }
    }

    #[test]
    fn command_lists_and_unknown_values_are_handled() {
        let mut dict = Dictionary::new();
        dict.insert(
            "command".into(),
            Value::Array(vec![
                Value::String("next".into()),
                Value::Integer(5.into()),
            ]),
        );
        assert_eq!(
            parse(&binary(dict)).commands,
            vec![RemoteCommand::NextTrack, RemoteCommand::PreviousTrack]
        );
        let mut dict = Dictionary::new();
        dict.insert("mrCommand".into(), Value::String("teleport".into()));
        let parsed = parse(&binary(dict));
        assert!(parsed.commands.is_empty());
        assert_eq!(parsed.source, None);
    }

    #[test]
    fn volume_is_normalised_from_each_wire_form() {
        for (value, expected) in [
            (Value::Real(0.25), 0.25),
            (Value::Integer(42.into()), 0.42),
            (Value::Real(-20.0), 0.1),
        ] {
            let mut dict = Dictionary::new();
            dict.insert("volume".into(), value);
            let parsed = parse(&binary(dict));
            let volume = parsed.volume.expect("volume");
            assert!((volume - expected).abs() < 0.01, "{volume} vs {expected}");
        }
    }

    #[test]
    fn garbage_and_empty_bodies_never_fail_the_channel() {
        assert_eq!(parse(&[]), ParsedEvent::default());
        assert_eq!(parse(b"not a plist"), ParsedEvent::default());
        let mut bytes = Vec::new();
        Value::String("hello".into())
            .to_writer_binary(&mut bytes)
            .unwrap();
        assert_eq!(parse(&bytes), ParsedEvent::default());
    }

    #[test]
    fn inbox_is_bounded() {
        let mut inbox = Inbox::default();
        for _ in 0..(Inbox::LIMIT + 5) {
            let mut dict = Dictionary::new();
            dict.insert("mrCommand".into(), Value::String("play".into()));
            inbox.push(parse(&binary(dict)));
        }
        assert_eq!(inbox.len(), Inbox::LIMIT);
        assert_eq!(inbox.dropped(), 5);
        assert_eq!(inbox.drain().len(), Inbox::LIMIT);
        assert!(inbox.is_empty());
    }
}

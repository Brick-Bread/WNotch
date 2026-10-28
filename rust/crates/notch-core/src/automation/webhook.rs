//! The rules of the local webhook. Ported from `src/Notch.Core/Automation/WebhookRequest.cs`.
//!
//! There are no sockets in here so it can be tested: who may call, what the paths are, and what a
//! body may say. The server (in the Windows crate) only moves bytes, and keeps the rate limit and
//! the body size limit while reading.

use std::time::Duration;

use serde::de::{Deserialize, Deserializer, MapAccess, Visitor};
use serde_json::value::RawValue;

use super::command::NotchCommand;
use super::parser::{self, MAX_DETAIL_LENGTH, MAX_GLYPH_LENGTH, MAX_TITLE_LENGTH, Values, utf16_len};
use crate::activities::{Activity, ActivityTier, Glow, GlowPattern};

pub const DEFAULT_PORT: u16 = 47890;
pub const MAX_BODY_BYTES: usize = 64 * 1024;
pub const MAX_ID_LENGTH: usize = 64;
pub const ACTIVITY_PREFIX: &str = "webhook.";
pub const MAX_TRANSIENT_LIFETIME: Duration = Duration::from_secs(5 * 60);

/// What a webhook request asks for, once it has been checked.
#[derive(Debug, Clone, PartialEq)]
pub enum WebhookAction {
    PublishActivity(Activity),
    RemoveActivity(String),
    RunCommand(NotchCommand),
}

/// A request that was turned away, with the HTTP status and a message for the caller.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WebhookRefusal {
    pub status: u16,
    pub message: String,
}

impl WebhookRefusal {
    fn new(status: u16, message: impl Into<String>) -> Self {
        Self { status, message: message.into() }
    }
}

/// Decides whether the caller may use the webhook at all. A browser page cannot pass this: it
/// always sends an `Origin`, and a page that rebinds its own name to this computer carries that
/// name in `Host`. Anything else must know the token.
pub fn authorize(
    host: Option<&str>,
    origin: Option<&str>,
    authorization: Option<&str>,
    token: Option<&str>,
    port: u16,
) -> Result<(), WebhookRefusal> {
    if origin.is_some_and(|origin| !origin.is_empty()) {
        return Err(WebhookRefusal::new(403, "Requests from web pages are not accepted."));
    }

    if !is_local_host(host, port) {
        return Err(WebhookRefusal::new(403, "Use the address 127.0.0.1 or localhost."));
    }

    let Some(token) = token.filter(|token| !token.is_empty()) else {
        return Err(WebhookRefusal::new(503, "The webhook has no token yet."));
    };

    const BEARER: &str = "Bearer ";
    let given = authorization
        .filter(|header| {
            header.len() >= BEARER.len() && header.as_bytes()[..BEARER.len()].eq_ignore_ascii_case(BEARER.as_bytes())
        })
        // The prefix is ASCII, so the rest starts on a character boundary.
        .map(|header| header[BEARER.len()..].trim());
    if !given.is_some_and(|given| same_secret(given, token)) {
        return Err(WebhookRefusal::new(401, "Send the token as Authorization: Bearer <token>."));
    }

    Ok(())
}

/// Reads a checked request.
pub fn route(method: &str, path: &str, body: Option<&str>) -> Result<WebhookAction, WebhookRefusal> {
    let clean = path.split('?').next().unwrap_or_default().trim_end_matches('/');

    if let Some(id) = clean.strip_prefix("/v1/activity/") {
        if !method.eq_ignore_ascii_case("DELETE") {
            return Err(WebhookRefusal::new(405, "Use DELETE to remove an activity."));
        }

        let id = parser::unescape(id);
        if !is_valid_id(Some(&id)) {
            return Err(WebhookRefusal::new(400, "That id is not valid."));
        }

        return Ok(WebhookAction::RemoveActivity(format!("{ACTIVITY_PREFIX}{id}")));
    }

    if !matches!(clean, "/v1/activity" | "/v1/notify" | "/v1/command") {
        return Err(WebhookRefusal::new(404, "Unknown path."));
    }

    if !method.eq_ignore_ascii_case("POST") {
        return Err(WebhookRefusal::new(405, "Use POST."));
    }

    let Some(body) = body.filter(|body| body.len() <= MAX_BODY_BYTES) else {
        return Err(WebhookRefusal::new(413, "The body is missing or too large."));
    };

    // JsonDocument.Parse refuses anything nested more than 64 deep.
    let root: &RawValue = match serde_json::from_str(body) {
        Ok(root) if depth(body) <= MAX_DEPTH => root,
        _ => return Err(invalid_json()),
    };

    if !root.get().starts_with('{') {
        return Err(WebhookRefusal::new(400, "The body must be a JSON object."));
    }
    let root = Object::read(root)?;

    match clean {
        "/v1/activity" => read_activity(&root),
        "/v1/notify" => read_command(Some("toast"), &root),
        _ => {
            let verb = match root.text("command")? {
                Some(verb) => Some(verb),
                None => root.text("verb")?,
            };
            read_command(verb.as_deref(), &root)
        }
    }
}

fn read_command(verb: Option<&str>, root: &Object) -> Result<WebhookAction, WebhookRefusal> {
    let mut values = Values::new();
    for (name, value) in &root.0 {
        let text = match kind(value) {
            Kind::String => Some(string(value)?),
            Kind::Number | Kind::Bool => Some(value.get().to_owned()),
            _ => None,
        };
        if let Some(text) = text {
            values.set(name, text);
        }
    }

    // The webhook may start timers and show notices; installing plugins stays a thing the user does at their own screen.
    let command = match verb {
        None => return Err(WebhookRefusal::new(400, "Say which command to run.")),
        Some(verb) => parser::build(verb, &values).map_err(|error| WebhookRefusal::new(400, error))?,
    };

    if matches!(command, NotchCommand::InstallPlugin { .. } | NotchCommand::SetPluginEnabled { .. }) {
        return Err(WebhookRefusal::new(403, "Plugins cannot be installed or switched through the webhook."));
    }

    Ok(WebhookAction::RunCommand(command))
}

fn read_activity(root: &Object) -> Result<WebhookAction, WebhookRefusal> {
    let id = root.text("id")?;
    let Some(id) = id.filter(|id| is_valid_id(Some(id))) else {
        return Err(WebhookRefusal::new(
            400,
            "An activity needs an id of letters, digits, dots, dashes or underscores.",
        ));
    };

    let title = root.text("title")?;
    let Some(title) = title.filter(|title| !title.trim().is_empty() && utf16_len(title) <= MAX_TITLE_LENGTH) else {
        return Err(WebhookRefusal::new(400, format!("The title must be 1 to {MAX_TITLE_LENGTH} characters.")));
    };

    let detail = root.text("detail")?;
    if detail.as_deref().is_some_and(|detail| utf16_len(detail) > MAX_DETAIL_LENGTH) {
        return Err(WebhookRefusal::new(400, format!("The detail can be at most {MAX_DETAIL_LENGTH} characters.")));
    }

    let glyph = root.text("glyph")?;
    if glyph.as_deref().is_some_and(|glyph| utf16_len(glyph) > MAX_GLYPH_LENGTH) {
        return Err(WebhookRefusal::new(400, "The glyph is one symbol."));
    }

    let tier = match root.text("tier")?.map(|tier| tier.to_lowercase()).as_deref() {
        None | Some("transient") => ActivityTier::Transient,
        Some("ongoing") => ActivityTier::Ongoing,
        Some("attention") => ActivityTier::Attention,
        Some("glance") => ActivityTier::Glance,
        Some(_) => return Err(WebhookRefusal::new(400, "The tier is ongoing, attention, glance or transient.")),
    };

    let progress = root.number("progress").map(|value| if value.is_nan() { 0.0 } else { value.clamp(0.0, 1.0) });

    let mut glow = None;
    if let Some(value) = root.get("glow").filter(|value| kind(value) != Kind::Null) {
        glow = read_glow(value)?;
        if glow.is_none() {
            return Err(WebhookRefusal::new(400, "A glow is a colour name, #rrggbb, or { color, pattern, strength }."));
        }
    }

    let lifetime = root
        .get("lifetimeMs")
        .filter(|value| kind(value) == Kind::Number)
        .and_then(|value| value.get().parse::<i64>().ok())
        .filter(|&milliseconds| milliseconds > 0)
        .map(|milliseconds| Duration::from_millis(milliseconds as u64).min(MAX_TRANSIENT_LIFETIME));

    let mut activity = Activity::new(format!("{ACTIVITY_PREFIX}{id}"), tier, title.trim());
    activity.detail = detail.filter(|detail| !detail.trim().is_empty()).map(|detail| detail.trim().to_owned());
    activity.glyph = glyph.filter(|glyph| !glyph.is_empty());
    activity.progress = progress;
    activity.glow = glow;
    activity.lifetime = if tier == ActivityTier::Transient { lifetime } else { None };
    Ok(WebhookAction::PublishActivity(activity))
}

fn read_glow(value: &RawValue) -> Result<Option<Glow>, WebhookRefusal> {
    match kind(value) {
        Kind::String => Ok(parser::parse_color(&string(value)?).map(|color| Glow::new(color, GlowPattern::Steady))),
        Kind::Object => {
            let value = Object::read(value)?;
            let Some(chosen) = value.text("color")?.and_then(|color| parser::parse_color(&color)) else {
                return Ok(None);
            };

            let mut pattern = GlowPattern::Steady;
            if let Some(name) = value.text("pattern")? {
                match parse_pattern(&name) {
                    Some(parsed) => pattern = parsed,
                    None => return Ok(None),
                }
            }

            let strength = value.number("strength").map_or(1.0, |strength| strength.clamp(0.0, 1.0));
            Ok(Some(Glow::with_strength(chosen, pattern, strength)))
        }
        _ => Ok(None),
    }
}

/// `Enum.TryParse(name, ignoreCase: true)`, which also reads a number ("2") and names joined by
/// commas ("Breathe, Pulse"), whose values it combines. A value that is not a pattern is refused;
/// C# would accept it as a pattern that does not exist.
fn parse_pattern(name: &str) -> Option<GlowPattern> {
    let text = name.trim();
    let value = if text.starts_with(|c: char| c.is_ascii_digit() || c == '+' || c == '-') {
        text.parse::<i32>().ok()?
    } else {
        let mut value = 0;
        for part in text.split(',') {
            let part = part.trim();
            value |= GlowPattern::ALL.iter().position(|pattern| pattern.name().eq_ignore_ascii_case(part))? as i32;
        }
        value
    };
    usize::try_from(value).ok().and_then(|index| GlowPattern::ALL.get(index)).copied()
}

pub fn is_valid_id(id: Option<&str>) -> bool {
    let Some(id) = id.filter(|id| (1..=MAX_ID_LENGTH).contains(&utf16_len(id))) else {
        return false;
    };
    // The .NET pattern is `^[A-Za-z0-9._-]+$`, and `$` there also matches before a final line
    // break, so C# lets "abc\n" through. Kept so both apps accept the same ids.
    let id = id.strip_suffix('\n').unwrap_or(id);
    !id.is_empty() && id.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b'-'))
}

/// `127.0.0.1:port`, `localhost:port` or `[::1]:port`.
fn is_local_host(host: Option<&str>, port: u16) -> bool {
    host.is_some_and(|host| {
        [format!("127.0.0.1:{port}"), format!("localhost:{port}"), format!("[::1]:{port}")]
            .iter()
            .any(|local| host.eq_ignore_ascii_case(local))
    })
}

/// Compares in time that depends only on the length, like `CryptographicOperations.FixedTimeEquals`.
fn same_secret(given: &str, expected: &str) -> bool {
    let (given, expected) = (given.as_bytes(), expected.as_bytes());
    if given.len() != expected.len() {
        return false;
    }
    let difference = given.iter().zip(expected).fold(0u8, |difference, (a, b)| difference | (a ^ b));
    std::hint::black_box(difference) == 0
}

/// A new random secret for the webhook: 32 random bytes in URL-safe base64.
pub fn new_token() -> String {
    use base64::Engine;

    let mut bytes = [0u8; 32];
    getrandom::fill(&mut bytes).expect("the system has no source of randomness");
    base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(bytes)
}

const MAX_DEPTH: usize = 64;

fn invalid_json() -> WebhookRefusal {
    WebhookRefusal::new(400, "The body is not valid JSON.")
}

/// How deeply arrays and objects nest in `json`, which must be valid JSON.
fn depth(json: &str) -> usize {
    let (mut depth, mut deepest, mut in_string, mut escaped) = (0usize, 0, false, false);
    for byte in json.bytes() {
        if in_string {
            match byte {
                _ if escaped => escaped = false,
                b'\\' => escaped = true,
                b'"' => in_string = false,
                _ => {}
            }
            continue;
        }
        match byte {
            b'"' => in_string = true,
            b'[' | b'{' => {
                depth += 1;
                deepest = deepest.max(depth);
            }
            b']' | b'}' => depth -= 1,
            _ => {}
        }
    }
    deepest
}

/// A JSON object as `JsonDocument` holds one: every property in order, repeats included, each value
/// left as its source text until it is read. Commands see the raw text of numbers this way, and a
/// number nothing reads is never converted.
struct Object<'a>(Vec<(String, &'a RawValue)>);

impl<'a> Object<'a> {
    /// Fails, as reading a broken name or string does in C#, for a lone UTF-16 surrogate.
    fn read(raw: &'a RawValue) -> Result<Self, WebhookRefusal> {
        serde_json::from_str(raw.get()).map_err(|_| invalid_json())
    }

    /// `TryGetProperty`: the last property of that name.
    fn get(&self, name: &str) -> Option<&'a RawValue> {
        self.0.iter().rev().find(|(key, _)| key == name).map(|&(_, value)| value)
    }

    /// The property when it is a string.
    fn text(&self, name: &str) -> Result<Option<String>, WebhookRefusal> {
        self.get(name).filter(|value| kind(value) == Kind::String).map(string).transpose()
    }

    /// The property when it is a number, as `GetDouble` reads it.
    fn number(&self, name: &str) -> Option<f64> {
        self.get(name).filter(|value| kind(value) == Kind::Number).and_then(|value| value.get().parse().ok())
    }
}

impl<'de> Deserialize<'de> for Object<'de> {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct Properties;

        impl<'de> Visitor<'de> for Properties {
            type Value = Object<'de>;

            fn expecting(&self, formatter: &mut std::fmt::Formatter) -> std::fmt::Result {
                formatter.write_str("a JSON object")
            }

            fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<Self::Value, A::Error> {
                let mut properties = Vec::new();
                while let Some(property) = map.next_entry()? {
                    properties.push(property);
                }
                Ok(Object(properties))
            }
        }

        deserializer.deserialize_map(Properties)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Kind {
    Object,
    Array,
    String,
    Number,
    Bool,
    Null,
}

fn kind(value: &RawValue) -> Kind {
    match value.get().as_bytes().first() {
        Some(b'{') => Kind::Object,
        Some(b'[') => Kind::Array,
        Some(b'"') => Kind::String,
        Some(b't' | b'f') => Kind::Bool,
        Some(b'n') => Kind::Null,
        _ => Kind::Number,
    }
}

fn string(value: &RawValue) -> Result<String, WebhookRefusal> {
    serde_json::from_str(value.get()).map_err(|_| invalid_json())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::activities::GlowColor;

    const TOKEN: &str = "s3cret-token";
    const PORT: u16 = 47890;

    fn authorize_with(host: Option<&str>, origin: Option<&str>, auth: Option<&str>) -> Option<u16> {
        authorize(host, origin, auth, Some(TOKEN), PORT).err().map(|refusal| refusal.status)
    }

    fn bearer() -> String {
        format!("Bearer {TOKEN}")
    }

    #[test]
    fn a_local_caller_with_the_token_is_let_in() {
        assert_eq!(authorize_with(Some("127.0.0.1:47890"), None, Some(&bearer())), None);
        assert_eq!(authorize_with(Some("localhost:47890"), None, Some(&bearer())), None);
        assert_eq!(authorize_with(Some("127.0.0.1:47890"), None, Some(&format!("bearer {TOKEN}"))), None);
    }

    #[test]
    fn a_missing_or_wrong_token_is_refused() {
        for header in [None, Some(""), Some("Bearer wrong"), Some("Bearer "), Some("Basic s3cret-token"), Some(TOKEN)] {
            assert_eq!(authorize_with(Some("127.0.0.1:47890"), None, header), Some(401), "{header:?}");
        }
    }

    #[test]
    fn web_pages_are_turned_away_even_with_the_token() {
        assert_eq!(authorize_with(Some("127.0.0.1:47890"), Some("https://evil.example"), Some(&bearer())), Some(403));
        assert_eq!(authorize_with(Some("127.0.0.1:47890"), Some("null"), Some(&bearer())), Some(403));
    }

    #[test]
    fn a_name_that_was_rebound_to_this_computer_is_refused() {
        for host in [Some("evil.example:47890"), Some("evil.example"), Some("127.0.0.1"), Some("127.0.0.1:1"), None] {
            assert_eq!(authorize_with(host, None, Some(&bearer())), Some(403), "{host:?}");
        }
    }

    #[test]
    fn without_a_token_set_nothing_is_accepted() {
        assert_eq!(authorize(Some("127.0.0.1:47890"), None, Some("Bearer "), Some(""), PORT).unwrap_err().status, 503);
    }

    fn route_ok(method: &str, path: &str, body: Option<&str>) -> WebhookAction {
        route(method, path, body).unwrap()
    }

    fn refuse(method: &str, path: &str, body: Option<&str>) -> u16 {
        route(method, path, body).err().map_or(0, |refusal| refusal.status)
    }

    fn published(action: WebhookAction) -> Activity {
        match action {
            WebhookAction::PublishActivity(activity) => activity,
            other => panic!("expected an activity, got {other:?}"),
        }
    }

    fn command(action: WebhookAction) -> NotchCommand {
        match action {
            WebhookAction::RunCommand(command) => command,
            other => panic!("expected a command, got {other:?}"),
        }
    }

    #[test]
    fn an_activity_is_namespaced_and_clamped() {
        let activity = published(route_ok(
            "POST",
            "/v1/activity",
            Some(
                r#"{ "id": "build", "title": "Building", "detail": "step 2", "glyph": "!", "tier": "ongoing",
                  "progress": 3, "glow": { "color": "Cyan", "pattern": "breathe", "strength": 0.5 } }"#,
            ),
        ));
        assert_eq!(activity.id, "webhook.build");
        assert_eq!(activity.tier, ActivityTier::Ongoing);
        assert_eq!(activity.progress, Some(1.0));
        assert_eq!(activity.glow, Some(Glow::with_strength(GlowColor::CYAN, GlowPattern::Breathe, 0.5)));
        assert_eq!(activity.lifetime, None);
    }

    #[test]
    fn an_activity_can_join_the_glance_row() {
        let activity = published(route_ok(
            "POST",
            "/v1/activity",
            Some(r#"{ "id": "ci", "title": "3 failing", "tier": "glance" }"#),
        ));
        assert_eq!(activity.tier, ActivityTier::Glance);
        assert_eq!(activity.lifetime, None);
    }

    #[test]
    fn a_plain_activity_is_transient_with_a_capped_lifetime() {
        let activity = published(route_ok(
            "POST",
            "/v1/activity",
            Some(r#"{ "id": "a", "title": "T", "lifetimeMs": 99999999, "glow": "green" }"#),
        ));
        assert_eq!(activity.tier, ActivityTier::Transient);
        assert_eq!(activity.lifetime, Some(MAX_TRANSIENT_LIFETIME));
        assert_eq!(activity.glow.unwrap().color, GlowColor::GREEN);
    }

    #[test]
    fn a_bad_activity_is_refused_with_400() {
        for body in [
            r#"{ "title": "no id" }"#,
            r#"{ "id": "has space", "title": "T" }"#,
            r#"{ "id": "a", "title": "" }"#,
            r#"{ "id": "a", "title": "T", "tier": "urgent" }"#,
            r#"{ "id": "a", "title": "T", "glow": "sparkly" }"#,
            r#"{ "id": "a", "title": "T", "glow": { "color": "red", "pattern": "wobble" } }"#,
            "[1]",
            "nope",
        ] {
            assert_eq!(refuse("POST", "/v1/activity", Some(body)), 400, "{body}");
        }
    }

    #[test]
    fn an_activity_can_be_removed_by_id() {
        assert_eq!(
            route_ok("DELETE", "/v1/activity/build", None),
            WebhookAction::RemoveActivity("webhook.build".into())
        );
        assert_eq!(refuse("DELETE", "/v1/activity/bad%20id", None), 400);
        assert_eq!(refuse("GET", "/v1/activity/build", None), 405);
    }

    #[test]
    fn notify_and_command_use_the_shared_commands() {
        let toast = command(route_ok("POST", "/v1/notify", Some(r#"{ "title": "Done", "color": "green" }"#)));
        assert!(matches!(toast, NotchCommand::Toast(_)));

        let timer = command(route_ok("POST", "/v1/command", Some(r#"{ "command": "timer", "d": "5m" }"#)));
        assert_eq!(timer, NotchCommand::StartTimer { duration: Duration::from_secs(5 * 60) });
    }

    #[test]
    fn plugins_cannot_be_installed_or_switched_from_the_webhook() {
        assert_eq!(refuse("POST", "/v1/command", Some(r#"{ "command": "install", "id": "acme.x" }"#)), 403);
        assert_eq!(
            refuse("POST", "/v1/command", Some(r#"{ "command": "plugin", "id": "acme.x", "enable": true }"#)),
            403
        );
    }

    #[test]
    fn unknown_paths_and_methods_are_refused() {
        assert_eq!(refuse("POST", "/v2/activity", Some("{}")), 404);
        assert_eq!(refuse("POST", "/", Some("{}")), 404);
        assert_eq!(refuse("GET", "/v1/notify", None), 405);
        assert_eq!(refuse("POST", "/v1/command", Some(r#"{ "command": "fly" }"#)), 400);
        assert_eq!(refuse("POST", "/v1/command", Some("{}")), 400);
    }

    #[test]
    fn a_missing_or_huge_body_is_refused() {
        assert_eq!(refuse("POST", "/v1/notify", None), 413);
        assert_eq!(refuse("POST", "/v1/notify", Some(&"x".repeat(MAX_BODY_BYTES + 1))), 413);
    }

    #[test]
    fn tokens_are_long_and_different_each_time() {
        let a = new_token();
        assert!(a.len() >= 40);
        assert_ne!(a, new_token());
        assert!(!a.contains('+'));
        assert!(!a.contains('/'));
    }

    // The tests below pin down C# details the ported tests do not reach.

    #[test]
    fn json_is_read_as_system_text_json_reads_it() {
        // Numbers reach commands as written, and the last of a repeated property wins.
        let timer = command(route_ok("POST", "/v1/command", Some(r#"{ "command": "timer", "d": 5, "d": "90s" }"#)));
        assert_eq!(timer, NotchCommand::StartTimer { duration: Duration::from_secs(90) });
        assert_eq!(refuse("POST", "/v1/command", Some(r#"{ "verb": "timer", "d": 1e3 }"#)), 400);

        // At most 64 levels of nesting.
        let nested = |levels: usize| format!(r#"{{"x":{}0{}}}"#, "[".repeat(levels - 1), "]".repeat(levels - 1));
        let deep = format!(r#"{{"command":"palette","x":{}0{}}}"#, "[".repeat(63), "]".repeat(63));
        assert_eq!(command(route_ok("POST", "/v1/command", Some(&deep))), NotchCommand::OpenPalette);
        assert_eq!(route("POST", "/v1/command", Some(&nested(65))).unwrap_err().message, "The body is not valid JSON.");
    }

    #[test]
    fn lifetimes_and_patterns_follow_dotnet_parsing() {
        let activity = published(route_ok(
            "POST",
            "/v1/activity",
            Some(
                r#"{ "id": "a", "title": " T ", "detail": "  ", "lifetimeMs": 1.5, "glow": { "color": "red", "pattern": "Breathe, Pulse" } }"#,
            ),
        ));
        assert_eq!(activity.title, "T");
        assert_eq!(activity.detail, None);
        assert_eq!(activity.lifetime, None);
        assert_eq!(activity.glow.unwrap().pattern, GlowPattern::Flash);
        assert_eq!(parse_pattern("2"), Some(GlowPattern::Pulse));
        assert_eq!(parse_pattern("9"), None);
        assert_eq!(parse_pattern(""), None);
    }

    #[test]
    fn an_id_may_end_in_one_line_break_as_the_dotnet_pattern_allows() {
        assert!(is_valid_id(Some("abc\n")));
        assert!(!is_valid_id(Some("\n")));
        assert!(!is_valid_id(Some("abc\n\n")));
        assert!(!is_valid_id(Some(&"a".repeat(65))));
    }
}

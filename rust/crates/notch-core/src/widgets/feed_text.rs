//! Turns what a web feed widget fetched into the text it shows. Ported from `FeedText` in
//! `src/Notch.Core/Widgets/HomeWidgetData.cs`.

use std::collections::HashMap;

use serde_json::value::RawValue;

pub const MAX_LENGTH: usize = 600;

/// With a `field`, the body is read as JSON and the field is followed by its dotted path (numbers
/// index arrays: `items.0.title`). Without one, HTML loses its tags and anything else is shown as
/// it is. Either way the result is cut to [`MAX_LENGTH`].
///
/// Errors when the body is not JSON, or has no such field.
pub fn extract(body: &str, field: Option<&str>) -> Result<String, String> {
    let text = match field.filter(|f| !f.trim().is_empty()) {
        Some(field) => read_field(body, field.trim())?,
        None if body.trim_start().starts_with('<') => {
            let text = html_decode(&strip_tags(&strip_scripts_and_styles(body)));
            text.split_whitespace().collect::<Vec<_>>().join(" ")
        }
        None => body.to_owned(),
    };

    let text = text.trim();
    let units: Vec<u16> = text.encode_utf16().collect();
    if units.len() > MAX_LENGTH {
        Ok(String::from_utf16_lossy(&units[..MAX_LENGTH]).trim_end().to_owned() + "…")
    } else {
        Ok(text.to_owned())
    }
}

/// Follows the path through the JSON. Values that are not strings are shown as they were written,
/// as C#'s `GetRawText` shows them.
fn read_field(body: &str, path: &str) -> Result<String, String> {
    let root: &RawValue = serde_json::from_str(body).map_err(|_| "The feed did not return JSON.".to_owned())?;
    let missing = || format!("The feed has no \"{path}\".");

    let mut element = root;
    for part in path.split('.').filter(|p| !p.is_empty()) {
        let raw = element.get();
        element = if raw.starts_with('[') {
            let items: Vec<&RawValue> = serde_json::from_str(raw).map_err(|_| missing())?;
            let index = (!part.is_empty() && part.bytes().all(|b| b.is_ascii_digit()))
                .then(|| part.parse::<i32>().ok())
                .flatten()
                .map(|i| i as usize);
            match index.and_then(|i| items.get(i)) {
                Some(&item) => item,
                None => return Err(missing()),
            }
        } else if raw.starts_with('{') {
            // A repeated name: the last one wins, as JsonElement.TryGetProperty finds it.
            let properties: HashMap<String, &RawValue> = serde_json::from_str(raw).map_err(|_| missing())?;
            match properties.get(part) {
                Some(&child) => child,
                None => return Err(missing()),
            }
        } else {
            return Err(missing());
        };
    }

    let raw = element.get();
    Ok(if raw.starts_with('"') {
        serde_json::from_str::<String>(raw).unwrap_or_default()
    } else if raw == "null" {
        String::new()
    } else if raw.starts_with('[') {
        let items: Vec<&RawValue> = serde_json::from_str(raw).unwrap_or_default();
        items
            .iter()
            .map(|i| {
                if i.get().starts_with('"') {
                    serde_json::from_str(i.get()).unwrap_or_default()
                } else {
                    i.get().to_owned()
                }
            })
            .collect::<Vec<String>>()
            .join(", ")
    } else {
        raw.to_owned()
    })
}

/// Replaces each `<script …>…</script>` and `<style …>…</style>` (ignoring case) with a space, as
/// the C# pattern `<(script|style)\b[^>]*>.*?</\1>` does.
fn strip_scripts_and_styles(html: &str) -> String {
    let lower = html.to_ascii_lowercase();
    let mut result = String::with_capacity(html.len());
    let mut copied = 0;
    let mut at = 0;
    while let Some(offset) = lower[at..].find('<') {
        let start = at + offset;
        at = start + 1;
        let Some(name) = ["script", "style"].into_iter().find(|n| lower[start + 1..].starts_with(n)) else { continue };

        // \b: the name must not run on into more of a word.
        let after = start + 1 + name.len();
        if html[after..].chars().next().is_some_and(|c| c.is_alphanumeric() || c == '_') {
            continue;
        }

        let Some(open_end) = html[after..].find('>').map(|i| after + i + 1) else { continue };
        let closer = format!("</{name}>");
        let Some(close) = lower[open_end..].find(&closer).map(|i| open_end + i + closer.len()) else { continue };

        result.push_str(&html[copied..start]);
        result.push(' ');
        copied = close;
        at = close;
    }

    result.push_str(&html[copied..]);
    result
}

/// Replaces each `<…>` with a space.
fn strip_tags(html: &str) -> String {
    let mut result = String::with_capacity(html.len());
    let mut rest = html;
    while let Some(start) = rest.find('<') {
        let Some(length) = rest[start..].find('>') else { break };
        result.push_str(&rest[..start]);
        result.push(' ');
        rest = &rest[start + length + 1..];
    }

    result.push_str(rest);
    result
}

/// `WebUtility.HtmlDecode`: the HTML 4 named entities and `&#…;`/`&#x…;` numbers, each ending in `;`.
fn html_decode(text: &str) -> String {
    if !text.contains('&') {
        return text.to_owned();
    }

    let mut result = String::with_capacity(text.len());
    let mut rest = text;
    while let Some(amp) = rest.find('&') {
        result.push_str(&rest[..amp]);
        rest = &rest[amp..];
        // The entity runs to the next ; unless another & comes first.
        let end = rest[1..].find([';', '&']).map(|i| i + 1);
        let decoded = match end {
            Some(end) if rest.as_bytes()[end] == b';' => entity(&rest[1..end]).map(|c| (c, end + 1)),
            _ => None,
        };
        match decoded {
            Some((c, length)) => {
                result.push(c);
                rest = &rest[length..];
            }
            None => {
                result.push('&');
                rest = &rest[1..];
            }
        }
    }

    result.push_str(rest);
    result
}

fn entity(name: &str) -> Option<char> {
    if let Some(number) = name.strip_prefix('#').filter(|n| !n.is_empty()) {
        let value = match number.strip_prefix(['x', 'X']) {
            Some(hex) if !hex.is_empty() && hex.bytes().all(|b| b.is_ascii_hexdigit()) => {
                u32::from_str_radix(hex, 16).ok()
            }
            Some(_) => None,
            // uint.TryParse with NumberStyles.Integer: white space around it and a + in front are allowed.
            None => {
                let number = number.trim_matches(|c: char| c == ' ' || ('\t'..='\r').contains(&c));
                let digits = number.strip_prefix('+').unwrap_or(number);
                (!digits.is_empty() && digits.bytes().all(|b| b.is_ascii_digit()))
                    .then(|| digits.parse().ok())
                    .flatten()
            }
        };
        return value.and_then(char::from_u32);
    }

    ENTITIES.iter().find(|(n, _)| *n == name).and_then(|&(_, c)| char::from_u32(c))
}

/// The entities `WebUtility.HtmlDecode` knows: those of HTML 4, plus `apos`.
const ENTITIES: &[(&str, u32)] = &[
    ("quot", 34),
    ("amp", 38),
    ("apos", 39),
    ("lt", 60),
    ("gt", 62),
    ("nbsp", 160),
    ("iexcl", 161),
    ("cent", 162),
    ("pound", 163),
    ("curren", 164),
    ("yen", 165),
    ("brvbar", 166),
    ("sect", 167),
    ("uml", 168),
    ("copy", 169),
    ("ordf", 170),
    ("laquo", 171),
    ("not", 172),
    ("shy", 173),
    ("reg", 174),
    ("macr", 175),
    ("deg", 176),
    ("plusmn", 177),
    ("sup2", 178),
    ("sup3", 179),
    ("acute", 180),
    ("micro", 181),
    ("para", 182),
    ("middot", 183),
    ("cedil", 184),
    ("sup1", 185),
    ("ordm", 186),
    ("raquo", 187),
    ("frac14", 188),
    ("frac12", 189),
    ("frac34", 190),
    ("iquest", 191),
    ("Agrave", 192),
    ("Aacute", 193),
    ("Acirc", 194),
    ("Atilde", 195),
    ("Auml", 196),
    ("Aring", 197),
    ("AElig", 198),
    ("Ccedil", 199),
    ("Egrave", 200),
    ("Eacute", 201),
    ("Ecirc", 202),
    ("Euml", 203),
    ("Igrave", 204),
    ("Iacute", 205),
    ("Icirc", 206),
    ("Iuml", 207),
    ("ETH", 208),
    ("Ntilde", 209),
    ("Ograve", 210),
    ("Oacute", 211),
    ("Ocirc", 212),
    ("Otilde", 213),
    ("Ouml", 214),
    ("times", 215),
    ("Oslash", 216),
    ("Ugrave", 217),
    ("Uacute", 218),
    ("Ucirc", 219),
    ("Uuml", 220),
    ("Yacute", 221),
    ("THORN", 222),
    ("szlig", 223),
    ("agrave", 224),
    ("aacute", 225),
    ("acirc", 226),
    ("atilde", 227),
    ("auml", 228),
    ("aring", 229),
    ("aelig", 230),
    ("ccedil", 231),
    ("egrave", 232),
    ("eacute", 233),
    ("ecirc", 234),
    ("euml", 235),
    ("igrave", 236),
    ("iacute", 237),
    ("icirc", 238),
    ("iuml", 239),
    ("eth", 240),
    ("ntilde", 241),
    ("ograve", 242),
    ("oacute", 243),
    ("ocirc", 244),
    ("otilde", 245),
    ("ouml", 246),
    ("divide", 247),
    ("oslash", 248),
    ("ugrave", 249),
    ("uacute", 250),
    ("ucirc", 251),
    ("uuml", 252),
    ("yacute", 253),
    ("thorn", 254),
    ("yuml", 255),
    ("OElig", 338),
    ("oelig", 339),
    ("Scaron", 352),
    ("scaron", 353),
    ("Yuml", 376),
    ("fnof", 402),
    ("circ", 710),
    ("tilde", 732),
    ("Alpha", 913),
    ("Beta", 914),
    ("Gamma", 915),
    ("Delta", 916),
    ("Epsilon", 917),
    ("Zeta", 918),
    ("Eta", 919),
    ("Theta", 920),
    ("Iota", 921),
    ("Kappa", 922),
    ("Lambda", 923),
    ("Mu", 924),
    ("Nu", 925),
    ("Xi", 926),
    ("Omicron", 927),
    ("Pi", 928),
    ("Rho", 929),
    ("Sigma", 931),
    ("Tau", 932),
    ("Upsilon", 933),
    ("Phi", 934),
    ("Chi", 935),
    ("Psi", 936),
    ("Omega", 937),
    ("alpha", 945),
    ("beta", 946),
    ("gamma", 947),
    ("delta", 948),
    ("epsilon", 949),
    ("zeta", 950),
    ("eta", 951),
    ("theta", 952),
    ("iota", 953),
    ("kappa", 954),
    ("lambda", 955),
    ("mu", 956),
    ("nu", 957),
    ("xi", 958),
    ("omicron", 959),
    ("pi", 960),
    ("rho", 961),
    ("sigmaf", 962),
    ("sigma", 963),
    ("tau", 964),
    ("upsilon", 965),
    ("phi", 966),
    ("chi", 967),
    ("psi", 968),
    ("omega", 969),
    ("thetasym", 977),
    ("upsih", 978),
    ("piv", 982),
    ("ensp", 8194),
    ("emsp", 8195),
    ("thinsp", 8201),
    ("zwnj", 8204),
    ("zwj", 8205),
    ("lrm", 8206),
    ("rlm", 8207),
    ("ndash", 8211),
    ("mdash", 8212),
    ("lsquo", 8216),
    ("rsquo", 8217),
    ("sbquo", 8218),
    ("ldquo", 8220),
    ("rdquo", 8221),
    ("bdquo", 8222),
    ("dagger", 8224),
    ("Dagger", 8225),
    ("bull", 8226),
    ("hellip", 8230),
    ("permil", 8240),
    ("prime", 8242),
    ("Prime", 8243),
    ("lsaquo", 8249),
    ("rsaquo", 8250),
    ("oline", 8254),
    ("frasl", 8260),
    ("euro", 8364),
    ("image", 8465),
    ("weierp", 8472),
    ("real", 8476),
    ("trade", 8482),
    ("alefsym", 8501),
    ("larr", 8592),
    ("uarr", 8593),
    ("rarr", 8594),
    ("darr", 8595),
    ("harr", 8596),
    ("crarr", 8629),
    ("lArr", 8656),
    ("uArr", 8657),
    ("rArr", 8658),
    ("dArr", 8659),
    ("hArr", 8660),
    ("forall", 8704),
    ("part", 8706),
    ("exist", 8707),
    ("empty", 8709),
    ("nabla", 8711),
    ("isin", 8712),
    ("notin", 8713),
    ("ni", 8715),
    ("prod", 8719),
    ("sum", 8721),
    ("minus", 8722),
    ("lowast", 8727),
    ("radic", 8730),
    ("prop", 8733),
    ("infin", 8734),
    ("ang", 8736),
    ("and", 8743),
    ("or", 8744),
    ("cap", 8745),
    ("cup", 8746),
    ("int", 8747),
    ("there4", 8756),
    ("sim", 8764),
    ("cong", 8773),
    ("asymp", 8776),
    ("ne", 8800),
    ("equiv", 8801),
    ("le", 8804),
    ("ge", 8805),
    ("sub", 8834),
    ("sup", 8835),
    ("nsub", 8836),
    ("sube", 8838),
    ("supe", 8839),
    ("oplus", 8853),
    ("otimes", 8855),
    ("perp", 8869),
    ("sdot", 8901),
    ("lceil", 8968),
    ("rceil", 8969),
    ("lfloor", 8970),
    ("rfloor", 8971),
    ("lang", 9001),
    ("rang", 9002),
    ("loz", 9674),
    ("spades", 9824),
    ("clubs", 9827),
    ("hearts", 9829),
    ("diams", 9830),
];

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn feed_text_follows_a_json_path_or_strips_html() {
        let json = r#"{"data":{"status":"All good"},"items":[{"title":"First"},{"title":"Second"}],"count":3}"#;
        assert_eq!(extract(json, Some("data.status")).unwrap(), "All good");
        assert_eq!(extract(json, Some("items.1.title")).unwrap(), "Second");
        assert_eq!(extract(json, Some("count")).unwrap(), "3");
        assert_eq!(extract(json, Some("data.missing")), Err("The feed has no \"data.missing\".".into()));
        assert_eq!(extract("not json", Some("a")), Err("The feed did not return JSON.".into()));

        assert_eq!(
            extract("<html><style>p{}</style><p>Hello &amp;</p>\n<b>welcome</b></html>", None).unwrap(),
            "Hello & welcome"
        );
        assert_eq!(extract("  plain \n", Some("")).unwrap(), "plain");
        assert!(extract(&"x".repeat(2000), None).unwrap().ends_with('…'));
    }

    #[test]
    fn values_that_are_not_strings_show_as_written() {
        let json = r#"{"a":[1,"two",{"b" : true}],"n":null,"o":{"x" : 1.50}}"#;
        assert_eq!(extract(json, Some("a")).unwrap(), r#"1, two, {"b" : true}"#);
        assert_eq!(extract(json, Some("n")).unwrap(), "");
        assert_eq!(extract(json, Some("o")).unwrap(), r#"{"x" : 1.50}"#);
        assert_eq!(extract(json, Some(" .a..0. ")).unwrap(), "1");
        assert!(extract(json, Some("a.-1")).is_err());
        assert!(extract(json, Some("a.3")).is_err());
    }

    #[test]
    fn scripts_go_and_entities_decode() {
        assert_eq!(
            extract("<p>a<SCRIPT type=x>var s = '<b>';</script>b<scripts>c</scripts></p>", None).unwrap(),
            "a b c"
        );
        assert_eq!(
            extract("<p>&lt;&#65;&#x42;&eacute; &bogus; &amp &#xD800;</p>", None).unwrap(),
            "<ABé &bogus; &amp &#xD800;"
        );
        assert_eq!(extract("<p>&nbsp;x&nbsp;</p>", None).unwrap(), "x");
    }
}

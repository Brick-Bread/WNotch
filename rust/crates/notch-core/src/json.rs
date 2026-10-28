//! Reading JSON the way the C# app's System.Text.Json reads it into its types, so a file either
//! app wrote loads the same in both (`tests/fixtures/settings` records the C# answers).
//!
//! The rules that matter:
//! - property names match exactly, and for a repeated name the last one wins;
//! - a value of the wrong JSON type makes the whole document unusable, and the caller falls back
//!   to defaults as the C# app does;
//! - integers must be written as integers (`1.0` is refused) and fit in 32 bits;
//! - `null` is refused for numbers and booleans, and reads as "none" where the C# type allows it.
//!
//! One deliberate difference: where the C# type is a string or list that may not be null, C#
//! keeps a `null` from the file and then crashes or treats it as empty. Here it reads as empty.

use indexmap::IndexMap;
use serde_json::Value;

/// The document does not have the shape of the type being read.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Mismatch;

pub type Read<T> = Result<T, Mismatch>;

pub fn bool(value: &Value) -> Read<bool> {
    value.as_bool().ok_or(Mismatch)
}

pub fn int(value: &Value) -> Read<i32> {
    match value {
        Value::Number(n) => n.as_i64().and_then(|i| i32::try_from(i).ok()).ok_or(Mismatch),
        _ => Err(Mismatch),
    }
}

pub fn optional_int(value: &Value) -> Read<Option<i32>> {
    match value {
        Value::Null => Ok(None),
        _ => int(value).map(Some),
    }
}

/// A string the C# type declares as never null; `null` reads as empty.
pub fn string(value: &Value) -> Read<String> {
    Ok(optional_string(value)?.unwrap_or_default())
}

pub fn optional_string(value: &Value) -> Read<Option<String>> {
    match value {
        Value::Null => Ok(None),
        Value::String(s) => Ok(Some(s.clone())),
        _ => Err(Mismatch),
    }
}

/// A list; `null` reads as empty and `null` items are left out.
pub fn list<T>(value: &Value, item: impl Fn(&Value) -> Read<T>) -> Read<Vec<T>> {
    match value {
        Value::Null => Ok(Vec::new()),
        Value::Array(items) => items.iter().filter(|v| !v.is_null()).map(item).collect(),
        _ => Err(Mismatch),
    }
}

/// A `Dictionary<string, string>`, in the file's order; `null` values read as empty.
pub fn string_map(value: &Value) -> Read<IndexMap<String, String>> {
    match value {
        Value::Null => Ok(IndexMap::new()),
        Value::Object(map) => map.iter().map(|(k, v)| Ok((k.clone(), string(v)?))).collect(),
        _ => Err(Mismatch),
    }
}

pub fn string_list(value: &Value) -> Read<Vec<String>> {
    list(value, string)
}

pub fn to_string_list(items: &[String]) -> Value {
    Value::Array(items.iter().cloned().map(Value::String).collect())
}

pub fn to_optional_string(value: Option<&str>) -> Value {
    value.map_or(Value::Null, |s| Value::String(s.to_owned()))
}

/// Defines an enum stored the way `JsonStringEnumConverter` stores it: written as its name, read
/// from its name ignoring case, from its number, or from its number in a string. A number with no
/// name is kept as `Unknown` and written back as that number, as C# does.
macro_rules! json_enum {
    (
        $(#[$meta:meta])*
        $vis:vis enum $name:ident {
            $( $(#[$vmeta:meta])* $variant:ident = $number:literal, )+
        }
        default $default:ident;
    ) => {
        $(#[$meta])*
        #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
        $vis enum $name {
            $( $(#[$vmeta])* $variant, )+
            /// A number in the file that is none of the names.
            Unknown(i32),
        }

        impl Default for $name {
            fn default() -> Self {
                Self::$default
            }
        }

        impl $name {
            /// The name it is stored by, or `None` for [`Self::Unknown`].
            pub fn name(self) -> Option<&'static str> {
                match self {
                    $( Self::$variant => Some(stringify!($variant)), )+
                    Self::Unknown(_) => None,
                }
            }

            pub fn from_number(number: i32) -> Self {
                match number {
                    $( $number => Self::$variant, )+
                    other => Self::Unknown(other),
                }
            }

            pub fn number(self) -> i32 {
                match self {
                    $( Self::$variant => $number, )+
                    Self::Unknown(other) => other,
                }
            }

            /// The value its name stands for, ignoring case.
            pub fn from_name(name: &str) -> Option<Self> {
                $( if name.eq_ignore_ascii_case(stringify!($variant)) {
                    return Some(Self::$variant);
                } )+
                None
            }

            pub fn read(value: &serde_json::Value) -> $crate::json::Read<Self> {
                match value {
                    serde_json::Value::String(text) => Self::from_name(text.trim())
                        .or_else(|| text.trim().parse::<i32>().ok().map(Self::from_number))
                        .ok_or($crate::json::Mismatch),
                    serde_json::Value::Number(_) => $crate::json::int(value).map(Self::from_number),
                    _ => Err($crate::json::Mismatch),
                }
            }

            pub fn to_json(self) -> serde_json::Value {
                match self.name() {
                    Some(name) => serde_json::Value::String(name.to_owned()),
                    None => serde_json::Value::from(self.number()),
                }
            }
        }
    };
}

pub(crate) use json_enum;

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    json_enum! {
        enum Shape {
            Round = 0,
            Square = 1,
        }
        default Round;
    }

    #[test]
    fn integers_must_be_whole_and_fit_in_32_bits() {
        assert_eq!(int(&json!(7)), Ok(7));
        assert_eq!(int(&json!(-2147483648)), Ok(i32::MIN));
        assert_eq!(int(&json!(2147483648_i64)), Err(Mismatch));
        assert_eq!(int(&json!(1.0)), Err(Mismatch));
        assert_eq!(int(&json!("5")), Err(Mismatch));
        assert_eq!(int(&Value::Null), Err(Mismatch));
    }

    #[test]
    fn enums_read_names_numbers_and_numbers_in_strings() {
        assert_eq!(Shape::read(&json!("square")), Ok(Shape::Square));
        assert_eq!(Shape::read(&json!(1)), Ok(Shape::Square));
        assert_eq!(Shape::read(&json!("1")), Ok(Shape::Square));
        assert_eq!(Shape::read(&json!(7)), Ok(Shape::Unknown(7)));
        assert_eq!(Shape::read(&json!("Triangle")), Err(Mismatch));
        assert_eq!(Shape::read(&Value::Null), Err(Mismatch));
        assert_eq!(Shape::Unknown(7).to_json(), json!(7));
        assert_eq!(Shape::Square.to_json(), json!("Square"));
    }

    #[test]
    fn null_lists_and_strings_read_as_empty() {
        assert_eq!(string(&Value::Null), Ok(String::new()));
        assert_eq!(string_list(&Value::Null), Ok(vec![]));
        assert_eq!(string_list(&json!(["a", null, "b"])), Ok(vec!["a".into(), "b".into()]));
        assert_eq!(string_list(&json!(["a", 1])), Err(Mismatch));
    }
}

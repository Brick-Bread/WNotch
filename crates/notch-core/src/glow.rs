//! The light around the notch: colours, brightness patterns and how a brightness reaches the screen.

use serde::{Deserialize, Serialize};

/// An sRGB colour, 8 bits per channel.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct GlowColor {
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

impl GlowColor {
    pub const WHITE: GlowColor = GlowColor::new(0xF2, 0xF2, 0xF2);
    pub const BLUE: GlowColor = GlowColor::new(0x3D, 0x8B, 0xFF);
    pub const CYAN: GlowColor = GlowColor::new(0x2E, 0xC4, 0xFF);
    pub const GREEN: GlowColor = GlowColor::new(0x3F, 0xD8, 0x6B);
    pub const AMBER: GlowColor = GlowColor::new(0xFF, 0xB0, 0x1F);
    pub const ORANGE: GlowColor = GlowColor::new(0xFF, 0x7A, 0x2E);
    pub const RED: GlowColor = GlowColor::new(0xFF, 0x3B, 0x3B);
    pub const YELLOW: GlowColor = GlowColor::new(0xFF, 0xD8, 0x4A);
    pub const VIOLET: GlowColor = GlowColor::new(0xA8, 0x6B, 0xFF);

    /// A colour from its channels.
    pub const fn new(r: u8, g: u8, b: u8) -> Self {
        Self { r, g, b }
    }

    /// The presets by name, in the order a colour picker shows them.
    pub fn named() -> &'static [(&'static str, GlowColor)] {
        static NAMED: [(&str, GlowColor); 8] = [
            ("Blue", GlowColor::BLUE),
            ("Cyan", GlowColor::CYAN),
            ("Green", GlowColor::GREEN),
            ("Yellow", GlowColor::YELLOW),
            ("Amber", GlowColor::AMBER),
            ("Orange", GlowColor::ORANGE),
            ("Red", GlowColor::RED),
            ("Violet", GlowColor::VIOLET),
        ];
        &NAMED
    }

    /// The preset called `name` (case does not matter), or `None` when there is none.
    pub fn from_name(name: &str) -> Option<GlowColor> {
        Self::named()
            .iter()
            .find(|(key, _)| key.eq_ignore_ascii_case(name))
            .map(|&(_, color)| color)
    }

    /// The presets are picked to shine on black. This is the same colour deepened enough to
    /// read as text or a thin line on a light background.
    pub fn on_light(self) -> GlowColor {
        self.lerp(GlowColor::new(0, 0, 0), 0.3)
    }

    /// Linear blend: 0 gives this colour, 1 gives `other`.
    pub fn lerp(self, other: GlowColor, amount: f64) -> GlowColor {
        let amount = amount.clamp(0.0, 1.0);
        let mix = |from: u8, to: u8| {
            let (from, to) = (f64::from(from), f64::from(to));
            (from + (to - from) * amount).round() as u8
        };
        GlowColor::new(mix(self.r, other.r), mix(self.g, other.g), mix(self.b, other.b))
    }
}

/// How a glow's brightness moves over time.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum GlowPattern {
    /// Constant light.
    Steady,
    /// Slow rise and fall: something is in progress.
    Breathe,
    /// Quick, strong beats: something wants the user.
    Pulse,
    /// A bright burst that settles to a low glow: a one-off event.
    Flash,
    /// Follows the loudness of what is playing.
    Audio,
}

const BREATHE_PERIOD: f64 = 3.2;
const PULSE_PERIOD: f64 = 1.1;
const FLASH_DECAY: f64 = 1.2;

/// The light around the notch while an activity is on top.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Glow {
    pub color: GlowColor,
    pub pattern: GlowPattern,
    /// Overall brightness, 0..1.
    pub strength: f64,
}

impl Glow {
    /// Brightness (0..1) at `seconds` since the glow started. `audio_level` is the smoothed
    /// output loudness, 0..1; only [`GlowPattern::Audio`] uses it.
    pub fn intensity_at(&self, seconds: f64, audio_level: f64) -> f64 {
        let seconds = seconds.max(0.0);
        let intensity = match self.pattern {
            GlowPattern::Breathe => 0.45 + 0.55 * wave(seconds, BREATHE_PERIOD),
            GlowPattern::Pulse => 0.25 + 0.75 * wave(seconds, PULSE_PERIOD),
            GlowPattern::Flash => 0.35 + 0.65 * (1.0 - seconds / FLASH_DECAY).max(0.0),
            GlowPattern::Audio => 0.2 + 0.8 * audio_level.clamp(0.0, 1.0),
            GlowPattern::Steady => 0.8,
        };
        (intensity * self.strength).clamp(0.0, 1.0)
    }

    /// True when the brightness changes over time and needs a per-frame update.
    pub fn is_animated(&self) -> bool {
        self.pattern != GlowPattern::Steady
    }
}

/// 0 at the start of each period, 1 half way through, smooth in between.
fn wave(seconds: f64, period: f64) -> f64 {
    let phase = (std::f64::consts::PI * (seconds % period) / period).sin();
    phase * phase
}

/// How a glow's intensity is put on screen, including the user's brightness setting.
pub mod glow_output {
    pub const MIN_PERCENT: i32 = 25;
    pub const MAX_PERCENT: i32 = 200;
    pub const DEFAULT_PERCENT: i32 = 100;

    /// Below 1 this lifts dim glows much more than bright ones, so a subtle glow is still
    /// clearly visible while the difference between subtle and strong remains.
    const LIFT: f64 = 0.6;

    /// The user's setting as a multiplier, with anything out of range pulled back in.
    pub fn gain(percent: i32) -> f64 {
        f64::from(percent.clamp(MIN_PERCENT, MAX_PERCENT)) / 100.0
    }

    /// How strongly to draw a glow of `intensity` (0..1). 1 is full opacity; above that, which
    /// takes a `gain` over 1, there is only size left to add.
    pub fn level(intensity: f64, gain: f64) -> f64 {
        intensity.clamp(0.0, 1.0).powf(LIFT) * gain.max(0.0)
    }
}

/// Picks a glow colour that represents an image, such as album art.
pub struct AccentColor;

impl AccentColor {
    /// Weighted average of the image's colourful pixels, brightened to read as light. `None`
    /// for greyscale images. `bgra` holds 32-bit pixels in B, G, R, A order.
    pub fn from_pixels(bgra: &[u8]) -> Option<GlowColor> {
        let (mut r, mut g, mut b, mut weight) = (0.0_f64, 0.0_f64, 0.0_f64, 0.0_f64);
        let (pixels, _) = bgra.as_chunks::<4>();
        for pixel in pixels {
            if pixel[3] < 128 {
                continue;
            }

            let pb = f64::from(pixel[0]) / 255.0;
            let pg = f64::from(pixel[1]) / 255.0;
            let pr = f64::from(pixel[2]) / 255.0;
            let max = pr.max(pg).max(pb);
            let min = pr.min(pg).min(pb);
            let saturation = if max <= 0.0 { 0.0 } else { (max - min) / max };

            // Vivid, reasonably bright pixels describe an image better than its greys and shadows.
            let w = saturation * saturation * max;
            r += pr * w;
            g += pg * w;
            b += pb * w;
            weight += w;
        }

        if weight < 1e-3 {
            return None;
        }

        let (r, g, b) = (r / weight, g / weight, b / weight);

        // Scale up so the brightest channel is at full strength: a glow should never be murky.
        let peak = r.max(g).max(b);
        let scale = if peak > 0.0 { 1.0 / peak } else { 1.0 };
        let to_byte = |value: f64| (value.clamp(0.0, 1.0) * 255.0).round() as u8;
        Some(GlowColor::new(to_byte(r * scale), to_byte(g * scale), to_byte(b * scale)))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn glow(color: GlowColor, pattern: GlowPattern, strength: f64) -> Glow {
        Glow { color, pattern, strength }
    }

    fn near(a: f64, b: f64) -> bool {
        (a - b).abs() < 1e-6
    }

    #[test]
    fn flash_starts_bright_and_settles_low() {
        let g = glow(GlowColor::WHITE, GlowPattern::Flash, 1.0);
        assert!(near(g.intensity_at(0.0, 0.0), 1.0));
        assert!(near(g.intensity_at(5.0, 0.0), 0.35));
        assert!(g.intensity_at(0.3, 0.0) > g.intensity_at(0.9, 0.0));
    }

    #[test]
    fn waves_repeat_and_stay_in_range() {
        for (pattern, period) in [(GlowPattern::Breathe, 3.2), (GlowPattern::Pulse, 1.1)] {
            let g = glow(GlowColor::CYAN, pattern, 1.0);
            let mut t = 0.0;
            while t < period * 3.0 {
                let v = g.intensity_at(t, 0.0);
                assert!((0.0..=1.0).contains(&v));
                t += 0.05;
            }
            assert!(near(g.intensity_at(0.4, 0.0), g.intensity_at(0.4 + period, 0.0)));
            assert!(g.intensity_at(period / 2.0, 0.0) > g.intensity_at(0.0, 0.0));
        }
    }

    #[test]
    fn audio_glow_follows_the_level() {
        let g = glow(GlowColor::VIOLET, GlowPattern::Audio, 1.0);
        assert!(g.intensity_at(1.0, 0.9) > g.intensity_at(1.0, 0.1));
        assert!(near(g.intensity_at(1.0, 5.0), 1.0));
    }

    #[test]
    fn strength_scales_brightness() {
        assert!(near(glow(GlowColor::WHITE, GlowPattern::Steady, 0.5).intensity_at(0.0, 0.0), 0.4));
    }

    #[test]
    fn only_steady_is_not_animated() {
        assert!(!glow(GlowColor::WHITE, GlowPattern::Steady, 1.0).is_animated());
        assert!(glow(GlowColor::WHITE, GlowPattern::Breathe, 1.0).is_animated());
    }

    #[test]
    fn output_lifts_dim_glows_but_keeps_their_order() {
        assert!(near(glow_output::level(0.0, 1.0), 0.0));
        assert!(near(glow_output::level(1.0, 1.0), 1.0));
        assert!(glow_output::level(0.25, 1.0) > 0.4);
        assert!(glow_output::level(0.25, 1.0) < glow_output::level(0.5, 1.0));
    }

    #[test]
    fn output_scales_with_the_brightness_setting() {
        assert!(near(
            2.0 * glow_output::level(0.5, 1.0),
            glow_output::level(0.5, glow_output::gain(200))
        ));
        assert!(near(glow_output::gain(glow_output::DEFAULT_PERCENT), 1.0));
        assert!(near(glow_output::gain(-5), 0.25));
        assert!(near(glow_output::gain(1000), 2.0));
    }

    #[test]
    fn colours_blend() {
        assert_eq!(
            GlowColor::new(0, 0, 0).lerp(GlowColor::new(200, 128, 0), 0.5),
            GlowColor::new(100, 64, 0)
        );
    }

    #[test]
    fn accent_picks_the_vivid_colour_over_grey() {
        // Mostly grey pixels with a few strong blue ones (B, G, R, A order).
        let mut pixels = Vec::new();
        for _ in 0..90 {
            pixels.extend([128, 128, 128, 255]);
        }
        for _ in 0..10 {
            pixels.extend([220, 60, 20, 255]);
        }

        let accent = AccentColor::from_pixels(&pixels).expect("a vivid colour");
        assert_eq!(accent.b, 255);
        assert!(accent.r < 60 && accent.g < 100);
    }

    #[test]
    fn greyscale_images_have_no_accent() {
        assert_eq!(AccentColor::from_pixels(&[50, 50, 50, 255, 200, 200, 200, 255]), None);
    }

    #[test]
    fn presets_are_found_by_name() {
        for (name, found) in [("Blue", true), ("violet", true), ("None", false), ("", false)] {
            assert_eq!(GlowColor::from_name(name).is_some(), found, "{name}");
        }
    }

    #[test]
    fn every_named_preset_resolves_to_itself() {
        for &(name, color) in GlowColor::named() {
            assert_eq!(GlowColor::from_name(name), Some(color));
        }
    }

    #[test]
    fn on_light_deepens_every_channel() {
        let deep = GlowColor::YELLOW.on_light();
        assert!(deep.r < GlowColor::YELLOW.r && deep.g < GlowColor::YELLOW.g && deep.b < GlowColor::YELLOW.b);
    }

    #[test]
    fn glow_serialises_for_the_ui() {
        let json = serde_json::to_string(&glow(GlowColor::new(1, 2, 3), GlowPattern::Pulse, 0.5)).unwrap();
        assert_eq!(json, r#"{"color":{"r":1,"g":2,"b":3},"pattern":"pulse","strength":0.5}"#);
    }
}

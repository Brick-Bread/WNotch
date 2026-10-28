//! The light around the notch. Ported from `src/Notch.Core/Activities/Glow.cs`.

use std::f64::consts::PI;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct GlowColor {
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

impl GlowColor {
    pub const fn new(r: u8, g: u8, b: u8) -> Self {
        Self { r, g, b }
    }

    pub const WHITE: Self = Self::new(0xF2, 0xF2, 0xF2);
    pub const BLUE: Self = Self::new(0x3D, 0x8B, 0xFF);
    pub const CYAN: Self = Self::new(0x2E, 0xC4, 0xFF);
    pub const GREEN: Self = Self::new(0x3F, 0xD8, 0x6B);
    pub const AMBER: Self = Self::new(0xFF, 0xB0, 0x1F);
    pub const ORANGE: Self = Self::new(0xFF, 0x7A, 0x2E);
    pub const RED: Self = Self::new(0xFF, 0x3B, 0x3B);
    pub const YELLOW: Self = Self::new(0xFF, 0xD8, 0x4A);
    pub const VIOLET: Self = Self::new(0xA8, 0x6B, 0xFF);

    /// The presets by name, in the order a colour picker shows them. White is not among them.
    pub const NAMED: [(&'static str, Self); 8] = [
        ("Blue", Self::BLUE),
        ("Cyan", Self::CYAN),
        ("Green", Self::GREEN),
        ("Yellow", Self::YELLOW),
        ("Amber", Self::AMBER),
        ("Orange", Self::ORANGE),
        ("Red", Self::RED),
        ("Violet", Self::VIOLET),
    ];

    /// The preset called `name`, ignoring case, or `None` when there is none.
    pub fn from_name(name: &str) -> Option<Self> {
        Self::NAMED.iter().find(|(key, _)| key.eq_ignore_ascii_case(name)).map(|&(_, color)| color)
    }

    /// The presets are picked to shine on black. This is the same colour deepened enough to read
    /// as text or a thin line on a light background.
    pub fn on_light(self) -> Self {
        self.lerp(Self::new(0, 0, 0), 0.3)
    }

    /// Linear blend: 0 gives this colour, 1 gives `other`.
    pub fn lerp(self, other: Self, amount: f64) -> Self {
        let amount = amount.clamp(0.0, 1.0);
        let mix =
            |from: u8, to: u8| -> u8 { round(f64::from(from) + (f64::from(to) - f64::from(from)) * amount) as u8 };
        Self::new(mix(self.r, other.r), mix(self.g, other.g), mix(self.b, other.b))
    }

    /// `#rrggbb`, lowercase.
    pub fn to_hex(self) -> String {
        format!("#{:02x}{:02x}{:02x}", self.r, self.g, self.b)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum GlowPattern {
    /// Constant light.
    #[default]
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

impl GlowPattern {
    pub const ALL: [Self; 5] = [Self::Steady, Self::Breathe, Self::Pulse, Self::Flash, Self::Audio];

    pub fn name(self) -> &'static str {
        match self {
            Self::Steady => "Steady",
            Self::Breathe => "Breathe",
            Self::Pulse => "Pulse",
            Self::Flash => "Flash",
            Self::Audio => "Audio",
        }
    }
}

/// The light around the notch while an activity is on top.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Glow {
    pub color: GlowColor,
    pub pattern: GlowPattern,
    /// Overall brightness, 0..1.
    pub strength: f64,
}

impl Glow {
    const BREATHE_PERIOD: f64 = 3.2;
    const PULSE_PERIOD: f64 = 1.1;
    const FLASH_DECAY: f64 = 1.2;

    pub fn new(color: GlowColor, pattern: GlowPattern) -> Self {
        Self { color, pattern, strength: 1.0 }
    }

    pub fn with_strength(color: GlowColor, pattern: GlowPattern, strength: f64) -> Self {
        Self { color, pattern, strength }
    }

    /// Brightness (0..1) at `seconds` since the glow started. `audio_level` is the smoothed output
    /// loudness, 0..1, used only by [`GlowPattern::Audio`].
    pub fn intensity_at(&self, seconds: f64, audio_level: f64) -> f64 {
        let seconds = seconds.max(0.0);
        let intensity = match self.pattern {
            GlowPattern::Breathe => 0.45 + 0.55 * wave(seconds, Self::BREATHE_PERIOD),
            GlowPattern::Pulse => 0.25 + 0.75 * wave(seconds, Self::PULSE_PERIOD),
            GlowPattern::Flash => 0.35 + 0.65 * (1.0 - seconds / Self::FLASH_DECAY).max(0.0),
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

// 0 at the start of each period, 1 half way through, smooth in between.
fn wave(seconds: f64, period: f64) -> f64 {
    let phase = (PI * (seconds % period) / period).sin();
    phase * phase
}

/// How a glow's intensity is put on screen, including the user's brightness setting.
pub mod output {
    pub const MIN_PERCENT: i32 = 25;
    pub const MAX_PERCENT: i32 = 200;
    pub const DEFAULT_PERCENT: i32 = 100;

    // Below 1 this lifts dim glows much more than bright ones, so a subtle glow is still clearly
    // visible while the difference between subtle and strong remains.
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

/// A glow colour that represents an image, such as album art: the weighted average of its
/// colourful pixels, brightened to read as light. `None` for greyscale images.
///
/// `bgra` holds 32-bit pixels in B, G, R, A order.
pub fn accent_color(bgra: &[u8]) -> Option<GlowColor> {
    let (mut r, mut g, mut b, mut weight) = (0.0, 0.0, 0.0, 0.0);
    for pixel in bgra.chunks_exact(4) {
        if pixel[3] < 128 {
            continue;
        }

        let (pb, pg, pr) = (f64::from(pixel[0]) / 255.0, f64::from(pixel[1]) / 255.0, f64::from(pixel[2]) / 255.0);
        let max = pr.max(pg.max(pb));
        let min = pr.min(pg.min(pb));
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
    let peak = r.max(g.max(b));
    let scale = if peak > 0.0 { 1.0 / peak } else { 1.0 };
    let to_byte = |value: f64| round(value.clamp(0.0, 1.0) * 255.0) as u8;
    Some(GlowColor::new(to_byte(r * scale), to_byte(g * scale), to_byte(b * scale)))
}

/// `Math.Round`: halves go to the even neighbour.
pub(crate) fn round(value: f64) -> f64 {
    value.round_ties_even()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_ignore_case_and_white_is_not_a_preset() {
        assert_eq!(GlowColor::from_name("violet"), Some(GlowColor::VIOLET));
        assert_eq!(GlowColor::from_name("White"), None);
        assert_eq!(GlowColor::from_name(""), None);
    }

    #[test]
    fn on_light_deepens_by_thirty_percent() {
        assert_eq!(GlowColor::new(100, 200, 255).on_light(), GlowColor::new(70, 140, 178));
    }

    #[test]
    fn patterns_follow_their_curves() {
        let breathe = Glow::new(GlowColor::BLUE, GlowPattern::Breathe);
        assert!((breathe.intensity_at(0.0, 0.0) - 0.45).abs() < 1e-9);
        assert!((breathe.intensity_at(1.6, 0.0) - 1.0).abs() < 1e-9);

        let flash = Glow::new(GlowColor::BLUE, GlowPattern::Flash);
        assert!((flash.intensity_at(0.0, 0.0) - 1.0).abs() < 1e-9);
        assert!((flash.intensity_at(5.0, 0.0) - 0.35).abs() < 1e-9);

        let audio = Glow::with_strength(GlowColor::BLUE, GlowPattern::Audio, 0.5);
        assert!((audio.intensity_at(0.0, 2.0) - 0.5).abs() < 1e-9);
        assert!((Glow::new(GlowColor::BLUE, GlowPattern::Steady).intensity_at(-3.0, 0.0) - 0.8).abs() < 1e-9);
    }

    #[test]
    fn gain_is_clamped() {
        assert_eq!(output::gain(10), 0.25);
        assert_eq!(output::gain(9000), 2.0);
        assert_eq!(output::level(1.0, 1.5), 1.5);
    }

    #[test]
    fn accent_is_none_for_grey_and_bright_for_colour() {
        assert_eq!(accent_color(&[128, 128, 128, 255, 10, 10, 10, 255]), None);
        // A dark red pixel comes out as full red.
        assert_eq!(accent_color(&[0, 0, 100, 255]), Some(GlowColor::new(255, 0, 0)));
        // Transparent pixels do not count.
        assert_eq!(accent_color(&[0, 0, 255, 0]), None);
    }

    fn close(a: f64, b: f64) -> bool {
        (a - b).abs() < 1e-6
    }

    #[test]
    fn flash_starts_bright_and_settles_low() {
        let glow = Glow::new(GlowColor::WHITE, GlowPattern::Flash);

        assert!(close(glow.intensity_at(0.0, 0.0), 1.0));
        assert!(close(glow.intensity_at(5.0, 0.0), 0.35));
        assert!(glow.intensity_at(0.3, 0.0) > glow.intensity_at(0.9, 0.0));
    }

    #[test]
    fn waves_repeat_and_stay_in_range() {
        for (pattern, period) in [(GlowPattern::Breathe, 3.2), (GlowPattern::Pulse, 1.1)] {
            let glow = Glow::new(GlowColor::CYAN, pattern);

            let mut t = 0.0;
            while t < period * 3.0 {
                assert!((0.0..=1.0).contains(&glow.intensity_at(t, 0.0)));
                t += 0.05;
            }

            assert!(close(glow.intensity_at(0.4, 0.0), glow.intensity_at(0.4 + period, 0.0)));
            assert!(glow.intensity_at(period / 2.0, 0.0) > glow.intensity_at(0.0, 0.0));
        }
    }

    #[test]
    fn audio_glow_follows_the_level() {
        let glow = Glow::new(GlowColor::VIOLET, GlowPattern::Audio);

        assert!(glow.intensity_at(1.0, 0.9) > glow.intensity_at(1.0, 0.1));
        assert!(close(glow.intensity_at(1.0, 5.0), 1.0));
    }

    #[test]
    fn strength_scales_brightness() {
        assert!(close(Glow::with_strength(GlowColor::WHITE, GlowPattern::Steady, 0.5).intensity_at(0.0, 0.0), 0.4));
    }

    #[test]
    fn output_lifts_dim_glows_but_keeps_their_order() {
        assert!(close(output::level(0.0, 1.0), 0.0));
        assert!(close(output::level(1.0, 1.0), 1.0));
        assert!(output::level(0.25, 1.0) > 0.4);
        assert!(output::level(0.25, 1.0) < output::level(0.5, 1.0));
    }

    #[test]
    fn output_scales_with_the_brightness_setting() {
        assert!(close(2.0 * output::level(0.5, 1.0), output::level(0.5, output::gain(200))));
        assert_eq!(output::gain(output::DEFAULT_PERCENT), 1.0);
        assert_eq!(output::gain(-5), 0.25);
        assert_eq!(output::gain(1000), 2.0);
    }

    #[test]
    fn colours_blend() {
        assert_eq!(GlowColor::new(0, 0, 0).lerp(GlowColor::new(200, 128, 0), 0.5), GlowColor::new(100, 64, 0));
    }

    #[test]
    fn accent_picks_the_vivid_colour_over_grey() {
        // Mostly grey pixels with a few strong blue ones.
        let mut pixels = Vec::new();
        for _ in 0..90 {
            pixels.extend_from_slice(&[128, 128, 128, 255]);
        }
        for _ in 0..10 {
            pixels.extend_from_slice(&[220, 60, 20, 255]);
        }

        let accent = accent_color(&pixels).unwrap();

        assert_eq!(accent.b, 255);
        assert!(accent.r < 60 && accent.g < 100);
    }

    #[test]
    fn greyscale_images_have_no_accent() {
        assert_eq!(accent_color(&[50, 50, 50, 255, 200, 200, 200, 255]), None);
    }

    #[test]
    fn presets_are_found_by_name() {
        // C#'s null name is the empty string here.
        for (name, found) in [("Blue", true), ("violet", true), ("None", false), ("", false)] {
            assert_eq!(GlowColor::from_name(name).is_some(), found, "{name}");
        }
    }

    #[test]
    fn every_named_preset_resolves_to_itself() {
        for (name, color) in GlowColor::NAMED {
            assert_eq!(GlowColor::from_name(name), Some(color));
        }
    }

    #[test]
    fn on_light_deepens_every_channel() {
        let deep = GlowColor::YELLOW.on_light();

        assert!(deep.r < GlowColor::YELLOW.r && deep.g < GlowColor::YELLOW.g && deep.b < GlowColor::YELLOW.b);
    }
}

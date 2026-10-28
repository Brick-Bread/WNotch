//! Ported from `src/Notch.Core/Animation/Spring.cs`.

const MAX_STEP: f64 = 1.0 / 240.0;
const REST_DISTANCE: f64 = 0.05;
const REST_VELOCITY: f64 = 0.5;

/// A damped spring on a single value. Slightly underdamped by default for a small overshoot.
#[derive(Debug, Clone, PartialEq)]
pub struct Spring {
    value: f64,
    velocity: f64,
    pub target: f64,
    stiffness: f64,
    damping: f64,
}

impl Spring {
    pub const DEFAULT_STIFFNESS: f64 = 320.0;
    pub const DEFAULT_DAMPING: f64 = 28.0;

    /// A spring at rest on `initial`, with the default stiffness and damping.
    pub fn new(initial: f64) -> Self {
        Self::with(initial, Self::DEFAULT_STIFFNESS, Self::DEFAULT_DAMPING)
    }

    pub fn with(initial: f64, stiffness: f64, damping: f64) -> Self {
        Self { value: initial, velocity: 0.0, target: initial, stiffness, damping }
    }

    pub fn value(&self) -> f64 {
        self.value
    }

    pub fn velocity(&self) -> f64 {
        self.velocity
    }

    pub fn stiffness(&self) -> f64 {
        self.stiffness
    }

    pub fn damping(&self) -> f64 {
        self.damping
    }

    pub fn is_settled(&self) -> bool {
        (self.target - self.value).abs() < REST_DISTANCE && self.velocity.abs() < REST_VELOCITY
    }

    pub fn step(&mut self, seconds: f64) {
        // Sub-step so a dropped frame cannot make the integration blow up.
        let mut seconds = seconds.clamp(0.0, 0.1);
        while seconds > 0.0 {
            let dt = seconds.min(MAX_STEP);
            let acceleration = self.stiffness * (self.target - self.value) - self.damping * self.velocity;
            self.velocity += acceleration * dt;
            self.value += self.velocity * dt;
            seconds -= dt;
        }

        if self.is_settled() {
            self.snap_to_target();
        }
    }

    pub fn snap_to_target(&mut self) {
        self.value = self.target;
        self.velocity = 0.0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // The C# app has no tests for the spring; these pin down what PARITY.md describes.

    #[test]
    fn overshoots_a_little_then_settles_on_the_target() {
        let mut spring = Spring::new(0.0);
        spring.target = 100.0;

        let mut peak: f64 = 0.0;
        for _ in 0..240 {
            spring.step(1.0 / 60.0);
            peak = peak.max(spring.value());
        }

        assert!(peak > 100.0);
        assert!(spring.is_settled());
        assert_eq!(spring.value(), 100.0);
        assert_eq!(spring.velocity(), 0.0);
    }

    #[test]
    fn snaps_once_close_and_slow() {
        let mut spring = Spring::new(0.0);
        spring.target = 0.04;
        spring.step(0.0);

        assert_eq!(spring.value(), 0.04);
    }

    #[test]
    fn a_long_frame_moves_at_most_a_tenth_of_a_second() {
        let mut long = Spring::new(0.0);
        long.target = 100.0;
        long.step(5.0);

        let mut tenth = Spring::new(0.0);
        tenth.target = 100.0;
        tenth.step(0.1);

        assert_eq!(long, tenth);
    }
}

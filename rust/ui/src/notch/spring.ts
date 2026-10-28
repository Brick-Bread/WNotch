// A damped spring on one value: the same physics as notch_core::animation::Spring (and the C#
// Spring), so the app's copy of the island's size matches what is drawn.

const MAX_STEP = 1 / 240;
const REST_DISTANCE = 0.05;
const REST_VELOCITY = 0.5;

export class Spring {
  value: number;
  velocity = 0;
  target: number;

  constructor(
    initial: number,
    private readonly stiffness: number,
    private readonly damping: number,
  ) {
    this.value = initial;
    this.target = initial;
  }

  get settled(): boolean {
    return Math.abs(this.target - this.value) < REST_DISTANCE && Math.abs(this.velocity) < REST_VELOCITY;
  }

  step(seconds: number): void {
    // Sub-step so a dropped frame cannot make the integration blow up.
    let remaining = Math.min(Math.max(seconds, 0), 0.1);
    while (remaining > 0) {
      const dt = Math.min(remaining, MAX_STEP);
      const acceleration = this.stiffness * (this.target - this.value) - this.damping * this.velocity;
      this.velocity += acceleration * dt;
      this.value += this.velocity * dt;
      remaining -= dt;
    }
    if (this.settled) {
      this.value = this.target;
      this.velocity = 0;
    }
  }
}

export interface Shape {
  width: number;
  height: number;
  radius: number;
}

/// Width and height spring with a visible overshoot; the radius settles more calmly.
export class IslandAnimator {
  private readonly width: Spring;
  private readonly height: Spring;
  private readonly radius: Spring;

  constructor(start: Shape) {
    this.width = new Spring(start.width, 380, 26);
    this.height = new Spring(start.height, 380, 26);
    this.radius = new Spring(start.radius, 300, 30);
  }

  animateTo(target: Shape): void {
    this.width.target = target.width;
    this.height.target = target.height;
    this.radius.target = target.radius;
  }

  step(seconds: number): void {
    this.width.step(seconds);
    this.height.step(seconds);
    this.radius.step(seconds);
  }

  get shape(): Shape {
    return { width: this.width.value, height: this.height.value, radius: this.radius.value };
  }

  get running(): boolean {
    return !(this.width.settled && this.height.settled && this.radius.settled);
  }
}

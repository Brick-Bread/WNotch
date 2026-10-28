// What the app tells the notch's page; see crates/notch-app/src/view.rs.

export type Mode = "idle" | "compact" | "peek" | "expanded";
export type Tier = "glance" | "ongoing" | "attention" | "transient";
export type AgentState = "idle" | "working" | "needsInput" | "done";

export interface GlowView {
  color: string;
  pattern: "steady" | "breathe" | "pulse" | "flash" | "audio";
  strength: number;
}

export interface ActivityView {
  id: string;
  tier: Tier;
  title: string;
  detail: string | null;
  glyph: string | null;
  agentId: string | null;
  agentState: AgentState | null;
  image: string | null;
  progress: number | null;
  glow: GlowView | null;
  countingFrom: number | null;
  opensPage: string | null;
  order: number;
}

export interface ShellView {
  mode: Mode;
  expanded: boolean;
  atBottom: boolean;
  floating: boolean;
  edgeGap: number;
  sideInset: number;
  idleHeight: number;
  compactHeight: number;
  peekHeight: number;
  light: boolean;
  glowEffects: boolean;
  activities: ActivityView[];
}

export const MEDIA_ID = "media";

/// The pill's width for the activity it shows (src/Notch.App/Shell/NotchWindow.Pill.cs).
export function pillWidth(activity: ActivityView | undefined): number {
  if (!activity) return 360;
  if (activity.id === MEDIA_ID) return 380;
  if (activity.agentState === "needsInput") return 372;
  if (activity.agentState !== null) return 292;
  return 360;
}

/// "m:ss", or "h:mm:ss" from an hour on (src/Notch.Core/Activities/ElapsedClock.cs).
export function elapsed(fromMillis: number, nowMillis: number): string {
  const total = Math.max(0, Math.floor((nowMillis - fromMillis) / 1000));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor(total / 60) % 60;
  const seconds = String(total % 60).padStart(2, "0");
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, "0")}:${seconds}` : `${minutes}:${seconds}`;
}

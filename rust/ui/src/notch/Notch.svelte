<script lang="ts">
  // The notch: an island that springs between its shapes, the pill inside it, and (from step 4 on)
  // the expanded tabs. Ported from src/Notch.App/Shell/NotchWindow.xaml(.cs).
  import { invoke } from "@tauri-apps/api/core";
  import { listen } from "@tauri-apps/api/event";
  import { onDestroy, onMount } from "svelte";
  import Pill from "./Pill.svelte";
  import { IslandAnimator, type Shape } from "./spring";
  import { pillWidth, type ShellView } from "./state";

  const EXPANDED_RADIUS = 32;

  const IDLE: Shape = { width: 180, height: 10, radius: 5 };

  let view = $state<ShellView | null>(null);
  let shape = $state<Shape>(IDLE);
  let now = $state(Date.now());

  const animator = new IslandAnimator(IDLE);
  let frame = 0;
  let lastFrame = 0;
  let sentTarget = "";

  const top = $derived(view?.activities[0]);

  function radiusScale(): number {
    const value = parseFloat(getComputedStyle(document.documentElement).getPropertyValue("--island-radius-scale"));
    return Number.isFinite(value) ? Math.min(Math.max(value, 0), 2) : 1;
  }

  /// The shape each mode takes (NotchWindow.ShapeFor).
  function targetShape(v: ShellView): Shape {
    const scale = radiusScale();
    switch (v.mode) {
      case "compact":
        return { width: pillWidth(v.activities[0]), height: v.compactHeight, radius: (v.compactHeight / 2) * scale };
      case "peek":
        return { width: 380, height: v.peekHeight, radius: (v.peekHeight / 2) * scale };
      case "expanded":
        // Home is as tall as its widgets; the tabs come with step 4.
        return { width: 640, height: 300, radius: EXPANDED_RADIUS * scale };
      default:
        return { width: 180, height: v.idleHeight, radius: (v.idleHeight / 2) * scale };
    }
  }

  function animate(time: number) {
    const seconds = lastFrame === 0 ? 0 : (time - lastFrame) / 1000;
    lastFrame = time;
    animator.step(seconds);
    shape = animator.shape;
    frame = animator.running ? requestAnimationFrame(animate) : 0;
    if (!frame) lastFrame = 0;
  }

  function apply(next: ShellView) {
    view = next;
    document.documentElement.dataset.theme = next.light ? "light" : "dark";
    const target = targetShape(next);
    animator.animateTo(target);
    const key = `${target.width}x${target.height}x${target.radius}`;
    if (key !== sentTarget) {
      sentTarget = key;
      invoke("shape_target", { width: target.width, height: target.height, radius: target.radius });
    }
    if (!frame) frame = requestAnimationFrame(animate);
  }

  let unlisten: (() => void) | undefined;
  let clock: ReturnType<typeof setInterval> | undefined;

  onMount(async () => {
    unlisten = await listen<ShellView>("notch-state", (event) => apply(event.payload));
    clock = setInterval(() => (now = Date.now()), 1000);
    await invoke("shell_ready");
  });

  onDestroy(() => {
    unlisten?.();
    clearInterval(clock);
    cancelAnimationFrame(frame);
  });

  // The springs overshoot, which reads well when the island grows but would sink it below its
  // resting height (and under the notch's shoulders) as it shrinks; it stops at the resting height.
  const height = $derived(Math.max(view?.idleHeight ?? 10, shape.height));
  const width = $derived(Math.max(0, shape.width));
  const radius = $derived(Math.max(0, shape.radius));

  /// A notch is flush against its edge: the corners on that side are square.
  const corners = $derived.by(() => {
    if (!view || view.floating) return `${radius}px`;
    return view.atBottom ? `${radius}px ${radius}px 0 0` : `0 0 ${radius}px ${radius}px`;
  });

  const pill = $derived(view?.mode === "compact" || view?.mode === "peek");
</script>

{#if view}
  <div
    class="stage"
    class:bottom={view.atBottom}
    style:padding-top={view.atBottom ? "0" : `${view.edgeGap}px`}
    style:padding-bottom={view.atBottom ? `${view.edgeGap}px` : "0"}
    style:padding-left={view.atBottom ? `${view.sideInset}px` : "0"}
  >
    <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
    <div
      class="island"
      style:width="{width}px"
      style:height="{height}px"
      style:border-radius={corners}
      onclick={() => invoke("island_clicked")}
    >
      {#if !view.floating && !view.atBottom}
        <span class="shoulder left"></span>
        <span class="shoulder right"></span>
      {/if}

      <div class="layer" class:shown={pill}>
        {#if pill && top}
          {#key view.mode === "compact" && top.tier === "glance" ? "glance" : top.id}
            <Pill activity={top} {now} />
          {/key}
        {/if}
      </div>

      <div class="layer expanded" class:shown={view.mode === "expanded"}>
        <p class="placeholder">Home</p>
      </div>
    </div>
  </div>
{/if}

<style>
  :global(html),
  :global(body) {
    margin: 0;
    background: transparent;
    overflow: hidden;
    font-family: var(--ui-font-family);
    font-size: var(--ui-font-size);
    user-select: none;
    -webkit-font-smoothing: antialiased;
  }

  .stage {
    position: fixed;
    inset: 0;
    display: flex;
    justify-content: center;
    align-items: flex-start;
  }

  .stage.bottom {
    justify-content: flex-start;
    align-items: flex-end;
  }

  .island {
    position: relative;
    background: var(--island);
    overflow: visible;
    cursor: default;
  }

  /* The curves that join a notch to the screen edge on either side. */
  .shoulder {
    position: absolute;
    top: 0;
    width: 10px;
    height: 10px;
    background: radial-gradient(circle at 0 100%, transparent 10px, var(--island) 10.5px);
  }

  .shoulder.left {
    left: -10px;
    transform: scaleX(-1);
  }

  .shoulder.right {
    right: -10px;
  }

  .layer {
    position: absolute;
    inset: 0;
    overflow: hidden;
    border-radius: inherit;
    opacity: 0;
    transform: scale(0.96);
    /* Content leaves at once, and comes in once the island has mostly reached its new size. */
    transition:
      opacity 90ms linear,
      transform 0s 90ms;
    pointer-events: none;
  }

  .layer.shown {
    opacity: 1;
    transform: scale(1);
    transition:
      opacity 220ms cubic-bezier(0.33, 1, 0.68, 1) 130ms,
      transform 220ms cubic-bezier(0.33, 1, 0.68, 1) 130ms;
    pointer-events: auto;
  }

  .layer.expanded.shown {
    transition-delay: 170ms;
  }

  .placeholder {
    margin: 24px;
    color: var(--secondary-text);
  }
</style>

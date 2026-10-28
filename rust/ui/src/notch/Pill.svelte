<script lang="ts">
  // What the pill shows for its top activity: a HUD with a level bar, an agent's state, what is
  // playing, or an activity's title and detail. Ported from src/Notch.App/Shell/NotchWindow.Pill.cs.
  import { elapsed, MEDIA_ID, type ActivityView } from "./state";

  let { activity, now }: { activity: ActivityView; now: number } = $props();

  const level = $derived(activity.tier === "transient" && activity.progress !== null);
  const tint = $derived(activity.glow?.color ?? "var(--text)");
  const detail = $derived(activity.countingFrom !== null ? elapsed(activity.countingFrom, now) : activity.detail);
</script>

<div class="pill" class:level>
  <span class="icon">
    {#if activity.image}
      <img src={activity.image} alt="" />
    {:else if activity.agentId}
      <img class="logo" src={`/agents/${activity.agentId === "gemini" ? "geminicli" : activity.agentId}.svg`} alt="" />
    {:else if activity.glyph}
      <span class="glyph" style:color={tint}>{activity.glyph}</span>
    {/if}
  </span>

  {#if level}
    <span class="bar"><span class="fill" style:width="{(activity.progress ?? 0) * 100}%"></span></span>
    <span class="figure">{Math.round((activity.progress ?? 0) * 100)}</span>
  {:else}
    <span class="text">
      <span class="title">{activity.title}</span>
      {#if activity.detail && activity.agentState === null && activity.id === MEDIA_ID}
        <span class="secondary"> · {activity.detail}</span>
      {/if}
    </span>

    <span class="state">
      {#if activity.id === MEDIA_ID}
        <span class="wave" style:color={tint}>
          {#each [0, 1, 2, 3] as bar (bar)}<i style:animation-delay="{bar * 0.11}s"></i>{/each}
        </span>
      {:else if activity.agentState === "working"}
        <span class="dots">{#each [0, 1, 2] as dot (dot)}<i style:animation-delay="{dot * 0.2}s"></i>{/each}</span>
      {:else if activity.agentState === "needsInput"}
        <span class="attention">Needs you</span>
      {:else if activity.agentState === "done"}
        <span class="done"><span class="glyph">&#xE73E;</span> Done</span>
      {:else if detail}
        <span class="detail">{detail}</span>
      {/if}
    </span>
  {/if}
</div>

<style>
  .pill {
    display: flex;
    align-items: center;
    gap: 8px;
    height: 100%;
    padding: 0 12px 0 10px;
    color: var(--text);
    font-size: var(--ui-font-size);
    white-space: nowrap;
  }

  .icon {
    flex: none;
    display: grid;
    place-items: center;
    width: 18px;
    height: 18px;
  }

  .icon img {
    width: 18px;
    height: 18px;
    border-radius: 4px;
    object-fit: cover;
  }

  .icon .logo {
    border-radius: 0;
    object-fit: contain;
  }

  .glyph {
    font-family: var(--icon-font);
    font-size: 14px;
  }

  .text {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    font-weight: 500;
  }

  .secondary,
  .detail {
    color: var(--secondary-text);
    font-weight: 400;
  }

  .state {
    flex: none;
    max-width: 130px;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .bar {
    flex: none;
    width: 84px;
    height: 4px;
    border-radius: 2px;
    background: var(--track);
    overflow: hidden;
  }

  .fill {
    display: block;
    height: 100%;
    background: var(--text);
  }

  .figure {
    flex: 1;
    text-align: right;
    font-variant-numeric: tabular-nums;
    color: var(--secondary-text);
  }

  .attention {
    color: var(--attention);
    font-weight: 500;
  }

  .done {
    color: var(--done);
    font-weight: 500;
  }

  .wave,
  .dots {
    display: inline-flex;
    align-items: center;
    gap: 2px;
    height: 14px;
  }

  .wave i {
    width: 3px;
    height: 100%;
    border-radius: 2px;
    background: currentColor;
    animation: wave 0.45s ease-in-out infinite alternate;
  }

  .dots i {
    width: 4px;
    height: 4px;
    border-radius: 50%;
    background: var(--working);
    animation: blink 0.6s ease-in-out infinite alternate;
  }

  @keyframes wave {
    from {
      transform: scaleY(0.3);
    }
    to {
      transform: scaleY(1);
    }
  }

  @keyframes blink {
    from {
      opacity: 0.25;
    }
    to {
      opacity: 1;
    }
  }
</style>

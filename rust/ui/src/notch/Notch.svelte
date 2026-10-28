<script lang="ts">
  // The idle notch: 180 x 10, attached to the top edge (src/Notch.App/Shell/NotchWindow.xaml.cs:207).
  const idle = { width: 180, height: 10 };
</script>

<div
  class="island"
  style:width="{idle.width}px"
  style:height="{idle.height}px"
  style:border-radius="0 0 {idle.height / 2}px {idle.height / 2}px"
></div>

<style>
  :global(html),
  :global(body) {
    margin: 0;
    background: transparent;
    overflow: hidden;
  }

  .island {
    margin: 0 auto;
    background: var(--island);
  }
</style>

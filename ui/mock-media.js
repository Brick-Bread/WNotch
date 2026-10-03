// A fake player for the mock backend (`?mock=media`): the same twelve tracks as the C# demo
// service, drawn artwork, and a loudness that moves like music.

const ARTIST = 'Linkin Park';
const TRACKS = [
  ['In the End', 'Hybrid Theory', 216], ['Papercut', 'Hybrid Theory', 184], ['One Step Closer', 'Hybrid Theory', 155],
  ['Crawling', 'Hybrid Theory', 209], ['Numb', 'Meteora', 187], ['Faint', 'Meteora', 162],
  ['Somewhere I Belong', 'Meteora', 213], ['Breaking the Habit', 'Meteora', 196],
  ['What I\'ve Done', 'Minutes to Midnight', 205], ['New Divide', 'New Divide', 268],
  ['Burn It Down', 'Living Things', 230], ['The Emptiness Machine', 'From Zero', 190],
];

const ART = `data:image/svg+xml,${encodeURIComponent(
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128"><defs><linearGradient id="g" x1="0" y1="1" x2="1" y2="0">'
  + '<stop offset="0" stop-color="#78b4ff"/><stop offset="1" stop-color="#f0623c"/></linearGradient></defs>'
  + '<rect width="128" height="128" fill="url(#g)"/></svg>')}`;

/** Accent colour of the drawn artwork (what the backend would take from it). */
const ACCENT = { r: 255, g: 130, b: 95 };

/** @param {(name: string, payload: any) => void} emit */
export function createMockMedia(emit) {
  let index = 4;
  let media;

  const play = (position, isPlaying) => {
    const [title, album, seconds] = TRACKS[index];
    media = {
      sourceAppId: 'demo', title, artist: ARTIST, album, isPlaying,
      positionMs: position, positionUpdatedAtMs: Date.now(), durationMs: seconds * 1000, playbackRate: 1,
      art: ART, canTogglePlayPause: true, canGoNext: true, canGoPrevious: true, canSeek: true, hasTimeline: true,
    };
  };
  play(72_000, true);

  const activities = () => (media.isPlaying ? [{
    id: 'media', tier: 'ongoing', title: media.title, detail: media.artist, glyph: '♪', image: ART,
    glow: { color: ACCENT, pattern: 'audio', strength: 0.85 },
  }] : []);

  const publish = () => {
    emit('media', media);
    emit('activities', activities());
  };

  const position = () => (media.isPlaying ? media.positionMs + Date.now() - media.positionUpdatedAtMs : media.positionMs);

  return {
    activities,
    commands: {
      get_media: () => media,
      media_control: ({ action }) => {
        if (action === 'playPause') {
          play(position(), !media.isPlaying);
        } else {
          index = (index + (action === 'next' ? 1 : -1) + TRACKS.length) % TRACKS.length;
          play(0, media.isPlaying);
        }
        publish();
      },
      media_seek: ({ seconds }) => {
        play(seconds * 1000, media.isPlaying);
        publish();
      },
    },
    /** Starts the fake loudness. */
    start: () => setInterval(() => {
      if (media.isPlaying) {
        const t = Date.now() / 1000;
        const beat = Math.max(0, Math.sin(t * 7)) ** 2;
        emit('media-level', { level: Math.min(1, 0.15 + 0.6 * beat + 0.15 * Math.sin(t * 2.3) ** 2) });
      }
    }, 40),
  };
}

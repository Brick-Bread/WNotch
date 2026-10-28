//! Ported from `src/Notch.Core/Media/IMediaService.cs`.

use std::time::Duration;

use super::MediaSnapshot;

/// The system's media session, as the platform layer sees it.
///
/// The C# interface also raises `Changed` (on any thread) and returns a `Task` from each
/// control. Here the implementation tells the app itself when [`Self::current`] changes, by
/// whatever channel the app gives it, and the controls return at once: an implementation that
/// waits on the system does so on its own thread.
pub trait MediaService {
    /// `None` when no app has an active media session.
    fn current(&self) -> Option<MediaSnapshot>;

    fn toggle_play_pause(&self);

    fn next(&self);

    fn previous(&self);

    fn seek(&self, position: Duration);
}

//! Ported from `src/Notch.Core/Shelf/FileShelf.cs`.

/// As many as the shelf keeps; adding more drops the ones put there longest ago.
pub const CAPACITY: usize = 24;

/// Files and folders the user has put aside on the notch, to drag out again later. It holds their
/// paths, not copies.
///
/// In place of the C# `Changed` event, each method that can change the shelf returns whether it
/// did, and the caller redraws and saves when it gets `true`. The C# lock is the caller's
/// `Mutex`, if it needs one.
#[derive(Debug, Clone, Default)]
pub struct FileShelf {
    paths: Vec<String>,
}

impl FileShelf {
    /// `paths` is what the shelf held when it was last saved, newest first.
    pub fn new<I, S>(paths: I) -> Self
    where
        I: IntoIterator<Item = S>,
        S: AsRef<str>,
    {
        let mut paths = tidy(paths);
        paths.truncate(CAPACITY);
        Self { paths }
    }

    /// Puts paths at the front of the shelf, in the order given. One that is already there moves
    /// to the front instead of showing twice.
    pub fn add<I, S>(&mut self, paths: I) -> bool
    where
        I: IntoIterator<Item = S>,
        S: AsRef<str>,
    {
        let added = tidy(paths);
        if added.is_empty() {
            return false;
        }

        let mut next = added.clone();
        next.extend(self.paths.iter().filter(|path| !added.iter().any(|a| same_ignoring_case(a, path))).cloned());
        next.truncate(CAPACITY);

        if next == self.paths {
            return false;
        }

        self.paths = next;
        true
    }

    pub fn remove(&mut self, path: &str) -> bool {
        let before = self.paths.len();
        self.paths.retain(|p| !same_ignoring_case(p, path));
        self.paths.len() != before
    }

    pub fn clear(&mut self) -> bool {
        if self.paths.is_empty() {
            return false;
        }

        self.paths.clear();
        true
    }

    /// Drops what is no longer there, such as a file that was moved or deleted. `exists` says
    /// whether a path still leads to a file or folder.
    ///
    /// C# runs `exists` outside its lock because it may be slow. Here the caller that wants that
    /// checks a [`Self::snapshot`] off the lock and passes the answers in.
    pub fn prune(&mut self, mut exists: impl FnMut(&str) -> bool) -> bool {
        let gone: Vec<String> = self.paths.iter().filter(|path| !exists(path)).cloned().collect();
        if gone.is_empty() {
            return false;
        }

        let before = self.paths.len();
        self.paths.retain(|path| !gone.iter().any(|g| same_ignoring_case(g, path)));
        self.paths.len() != before
    }

    /// Newest first.
    pub fn snapshot(&self) -> Vec<String> {
        self.paths.clone()
    }
}

fn tidy<I, S>(paths: I) -> Vec<String>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    let mut tidied: Vec<String> = Vec::new();
    for path in paths {
        let path = path.as_ref().trim();
        if !path.is_empty() && !tidied.iter().any(|t| same_ignoring_case(t, path)) {
            tidied.push(path.to_owned());
        }
    }
    tidied
}

/// `StringComparison.OrdinalIgnoreCase`.
pub(crate) fn same_ignoring_case(a: &str, b: &str) -> bool {
    if a.is_ascii() && b.is_ascii() { a.eq_ignore_ascii_case(b) } else { a.to_lowercase() == b.to_lowercase() }
}

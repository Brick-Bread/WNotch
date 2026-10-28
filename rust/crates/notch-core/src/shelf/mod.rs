//! Files kept on the shelf. Ported from `src/Notch.Core/Shelf`.

pub mod activities;
pub mod file_shelf;
pub mod store;

pub use file_shelf::{CAPACITY, FileShelf};
pub use store::ShelfStore;

#[cfg(test)]
mod tests;

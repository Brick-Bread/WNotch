//! Messages between plugins: one publishes to a topic, every plugin subscribed to it hears about it.

use std::sync::{Mutex, MutexGuard};

/// Who listens to which topic. Topics are plain strings; plugins prefix theirs with their id.
#[derive(Default)]
pub struct Bus {
    subscriptions: Mutex<Vec<(String, String)>>,
}

impl Bus {
    pub fn new() -> Self {
        Self::default()
    }

    fn lock(&self) -> MutexGuard<'_, Vec<(String, String)>> {
        self.subscriptions.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Starts delivering a topic to a plugin. Subscribing twice is the same as once.
    pub fn subscribe(&self, owner: &str, topic: &str) {
        let mut subscriptions = self.lock();
        let entry = (owner.to_owned(), topic.to_owned());
        if !subscriptions.contains(&entry) {
            subscriptions.push(entry);
        }
    }

    /// Stops delivering a topic to a plugin.
    pub fn unsubscribe(&self, owner: &str, topic: &str) {
        self.lock().retain(|(o, t)| !(o == owner && t == topic));
    }

    /// Stops everything for a plugin, as when it stops.
    pub fn remove_owner(&self, owner: &str) {
        self.lock().retain(|(o, _)| o != owner);
    }

    /// The plugins a message on `topic` goes to, including the sender's own when it listens.
    pub fn recipients(&self, topic: &str) -> Vec<String> {
        self.lock().iter().filter(|(_, t)| t == topic).map(|(o, _)| o.clone()).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn messages_reach_subscribers_of_other_plugins_and_stop_when_a_plugin_does() {
        let bus = Bus::new();
        bus.subscribe("a", "weather");
        bus.subscribe("a", "weather");
        bus.subscribe("b", "weather");
        bus.subscribe("b", "other");

        assert_eq!(bus.recipients("weather"), ["a", "b"]);
        assert_eq!(bus.recipients("other"), ["b"]);
        assert!(bus.recipients("none").is_empty());

        bus.unsubscribe("a", "weather");
        assert_eq!(bus.recipients("weather"), ["b"]);

        bus.remove_owner("b");
        assert!(bus.recipients("weather").is_empty());
        assert!(bus.recipients("other").is_empty());
    }
}

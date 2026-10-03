//! The tray icon, which is how Notch is reached when the pill is hidden or off screen.
//!
//! A left click opens the settings; the menu (right click) has Settings, a Theme submenu with the
//! current theme ticked, and Quit.

use notch_core::settings::NotchTheme;
use tauri::image::Image;
use tauri::menu::{CheckMenuItem, Menu, MenuItem, PredefinedMenuItem, Submenu};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{AppHandle, Emitter, Listener, Manager};

use crate::commands::SettingsChanged;
use crate::state::Backend;

const ICON_SIZE: usize = 32;

/// The theme entries of the menu, whose ticks follow the settings.
struct ThemeItems(Vec<(NotchTheme, CheckMenuItem<tauri::Wry>)>);

pub fn create(app: &AppHandle) -> tauri::Result<()> {
    let settings = MenuItem::with_id(app, "settings", "Settings\u{2026}", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "Quit Notch", true, None::<&str>)?;

    let follow = if cfg!(windows) { "Follow Windows" } else { "Follow system" };
    let mut items = Vec::new();
    for (theme, id, name) in [
        (NotchTheme::Dark, "theme-dark", "Dark"),
        (NotchTheme::Light, "theme-light", "Light"),
        (NotchTheme::System, "theme-system", follow),
    ] {
        items.push((theme, CheckMenuItem::with_id(app, id, name, true, false, None::<&str>)?));
    }
    let theme_menu = Submenu::with_id(app, "theme", "Theme", true)?;
    for (_, item) in &items {
        theme_menu.append(item)?;
    }
    app.manage(ThemeItems(items));

    let separator = PredefinedMenuItem::separator(app)?;
    let menu = Menu::with_items(app, &[&settings, &theme_menu, &separator, &quit])?;

    let tray = TrayIconBuilder::new()
        .tooltip("Notch")
        .icon(Image::new_owned(pill_icon(), ICON_SIZE as u32, ICON_SIZE as u32))
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click { button: MouseButton::Left, button_state: MouseButtonState::Up, .. } = event {
                crate::show_settings(tray.app_handle());
            }
        })
        .on_menu_event(|app, event| match event.id().as_ref() {
            "settings" => crate::show_settings(app),
            "quit" => app.exit(0),
            "theme-dark" => choose_theme(app, NotchTheme::Dark),
            "theme-light" => choose_theme(app, NotchTheme::Light),
            "theme-system" => choose_theme(app, NotchTheme::System),
            _ => {}
        });
    tray.build(app)?;

    // The settings panel can change the theme too.
    let handle = app.clone();
    app.listen("settings", move |_| tick_current_theme(&handle));
    tick_current_theme(app);
    Ok(())
}

/// Saves the theme picked in the tray menu and tells the UI, which restyles itself.
fn choose_theme(app: &AppHandle, theme: NotchTheme) {
    let backend = app.state::<Backend>();
    let saved = {
        let mut settings = backend.settings();
        settings.theme = theme;
        settings.clone()
    };
    backend.store.save(&saved);
    let changed = SettingsChanged::new(saved);
    if let Err(e) = app.emit("settings", &changed) {
        log::warn!("could not emit settings: {e}");
    }
}

fn tick_current_theme(app: &AppHandle) {
    let current = app.state::<Backend>().settings().theme;
    for (theme, item) in &app.state::<ThemeItems>().0 {
        if let Err(e) = item.set_checked(*theme == current) {
            log::debug!("could not tick the theme: {e}");
        }
    }
}

/// The tray icon: a black pill with a white outline, as the C# app draws it. RGBA, `ICON_SIZE` square.
fn pill_icon() -> Vec<u8> {
    const SAMPLES: usize = 4;
    const RADIUS: f64 = 7.0;
    const OUTLINE: f64 = 2.0;
    let centre_y = 16.0;
    let (left, right) = (9.0, 23.0);

    // Distance from the pill's outline: negative inside it.
    let signed_distance = |x: f64, y: f64| {
        let along = x.clamp(left, right);
        ((x - along).powi(2) + (y - centre_y).powi(2)).sqrt() - RADIUS
    };

    let mut rgba = vec![0u8; ICON_SIZE * ICON_SIZE * 4];
    for py in 0..ICON_SIZE {
        for px in 0..ICON_SIZE {
            let (mut fill, mut ring) = (0usize, 0usize);
            for sy in 0..SAMPLES {
                for sx in 0..SAMPLES {
                    let x = px as f64 + (sx as f64 + 0.5) / SAMPLES as f64;
                    let y = py as f64 + (sy as f64 + 0.5) / SAMPLES as f64;
                    let distance = signed_distance(x, y);
                    if distance.abs() <= OUTLINE / 2.0 {
                        ring += 1;
                    } else if distance < 0.0 {
                        fill += 1;
                    }
                }
            }
            let total = (SAMPLES * SAMPLES) as f64;
            let covered = (fill + ring) as f64 / total;
            let white = if fill + ring == 0 { 0.0 } else { ring as f64 / (fill + ring) as f64 };
            let offset = (py * ICON_SIZE + px) * 4;
            let channel = (white * 255.0).round() as u8;
            rgba[offset..offset + 3].fill(channel);
            rgba[offset + 3] = (covered * 255.0).round() as u8;
        }
    }
    rgba
}

#[cfg(test)]
mod tests {
    use super::*;

    fn pixel(rgba: &[u8], x: usize, y: usize) -> [u8; 4] {
        let offset = (y * ICON_SIZE + x) * 4;
        [rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]]
    }

    #[test]
    fn the_icon_is_a_black_pill_with_a_white_rim() {
        let icon = pill_icon();
        assert_eq!(icon.len(), ICON_SIZE * ICON_SIZE * 4);
        assert_eq!(pixel(&icon, 16, 16), [0, 0, 0, 255], "inside is black");
        assert_eq!(pixel(&icon, 16, 9)[3], 255, "the rim is opaque");
        assert!(pixel(&icon, 16, 9)[0] > 200, "the rim is white");
        assert_eq!(pixel(&icon, 16, 2), [0, 0, 0, 0], "outside is clear");
        assert_eq!(pixel(&icon, 0, 16)[3], 0, "the pill does not reach the edge");
    }
}

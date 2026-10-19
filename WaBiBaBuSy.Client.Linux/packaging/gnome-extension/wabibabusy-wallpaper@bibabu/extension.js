// Fallback for GNOME Shell: pins WaBiBaBuSy wallpaper windows to the bottom of the window stack.
// The client already marks its windows as _NET_WM_WINDOW_TYPE_DESKTOP, which Mutter honours for
// XWayland/X11 windows; this extension is only for setups where that is not enough.
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

const WM_CLASS = 'wabibabusy-wallpaper';
const TITLE = 'WaBiBaBuSy Wallpaper';

export default class WaBiBaBuSyWallpaperExtension extends Extension {
    enable() {
        this._windowSignals = new Map();
        this._createdId = global.display.connect('window-created', (_display, win) => this._track(win));
        this._restackedId = global.display.connect('restacked', () => this._lowerAll());
        for (const actor of global.get_window_actors())
            this._track(actor.meta_window);
    }

    disable() {
        global.display.disconnect(this._createdId);
        global.display.disconnect(this._restackedId);
        for (const [win, id] of this._windowSignals)
            win.disconnect(id);
        this._windowSignals.clear();
        this._createdId = this._restackedId = 0;
    }

    _isWallpaper(win) {
        return win.get_wm_class() === WM_CLASS || win.get_wm_class_instance() === WM_CLASS || win.get_title() === TITLE;
    }

    _track(win) {
        if (!win || this._windowSignals.has(win))
            return;
        if (this._isWallpaper(win)) {
            this._pin(win);
            return;
        }
        // X11 clients may set WM_CLASS / title after the window is created
        const id = win.connect('notify::wm-class', () => {
            if (this._isWallpaper(win))
                this._pin(win);
        });
        this._windowSignals.set(win, id);
        win.connect('unmanaged', () => {
            const sid = this._windowSignals.get(win);
            if (sid)
                win.disconnect(sid);
            this._windowSignals.delete(win);
        });
    }

    _pin(win) {
        win.stick();
        win.make_below();
        win.lower();
        this._lowerActor(win);
    }

    _lowerAll() {
        for (const actor of global.get_window_actors()) {
            if (this._isWallpaper(actor.meta_window))
                this._lowerActor(actor.meta_window);
        }
    }

    _lowerActor(win) {
        const actor = win.get_compositor_private();
        const parent = actor?.get_parent();
        if (parent)
            parent.set_child_below_sibling(actor, null);
    }
}

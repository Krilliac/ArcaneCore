// Pointer and keyboard navigation of the viewport.
//   left drag  orbit (fly: look around; top: turn the heading)      right/middle drag  pan (shift: raise and lower)
//   wheel      zoom toward the cursor                                double click       centre on the surface under the cursor
//   W A S D    move (fly: along the look direction; orbit and top: over the ground)    Q E  down and up      shift  boost
// Camera maths lives in camera.js; this file only turns input into those calls.

import { lookAtPoint, moveCamera, orbitDrag, panGrab, panVertical, screenToNdc, zoomAt } from './camera.js';
import { clamp } from './util.js';

const DRAG_THRESHOLD = 4;
const MOVE_KEYS = new Set(['w', 'a', 's', 'd', 'q', 'e']);

/** True when keystrokes belong to a form control, not the viewport. */
export function isTypingTarget(target) {
    if (!(target instanceof Element)) {
        return false;
    }
    const tag = target.tagName;
    return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || target.isContentEditable;
}

export class Navigator {
    #drag = null;
    #keys = new Set();
    #keyFlight = false;
    #shift = false;
    #wheelTimer = null;
    #listeners = [];

    constructor({ app, viewport }) {
        this.app = app;
        this.viewport = viewport;
    }

    attach() {
        const { canvas } = this.viewport;
        const on = (target, type, handler, options) => {
            target.addEventListener(type, handler, options);
            this.#listeners.push(() => target.removeEventListener(type, handler, options));
        };
        on(canvas, 'pointerdown', (e) => this.#down(e));
        on(canvas, 'pointermove', (e) => this.#move(e));
        on(canvas, 'pointerup', (e) => this.#up(e));
        on(canvas, 'pointercancel', (e) => this.#up(e, true));
        on(canvas, 'pointerleave', () => {
            if (!this.#drag) {
                this.viewport.pointerLeft();
            }
        });
        on(canvas, 'dblclick', (e) => this.#doubleClick(e));
        on(canvas, 'wheel', (e) => this.#wheel(e), { passive: false });
        on(canvas, 'contextmenu', (e) => e.preventDefault());
        on(document, 'keydown', (e) => this.#keyDown(e));
        on(document, 'keyup', (e) => this.#keyUp(e));
        on(window, 'blur', () => this.#releaseKeys());
    }

    detach() {
        for (const off of this.#listeners) {
            off();
        }
        this.#listeners = [];
    }

    #local(e) {
        const rect = this.viewport.canvas.getBoundingClientRect();
        return { x: e.clientX - rect.left, y: e.clientY - rect.top, w: rect.width, h: rect.height };
    }

    #down(e) {
        if (this.#drag || (e.button !== 0 && e.button !== 1 && e.button !== 2)) {
            return;
        }
        e.preventDefault();
        this.viewport.canvas.setPointerCapture(e.pointerId);
        this.viewport.root.focus({ preventScroll: true });
        const p = this.#local(e);
        this.#drag = { id: e.pointerId, button: e.button, x0: p.x, y0: p.y, camera: this.app.view.camera, moved: false, shift: e.shiftKey };
        this.app.beginInteraction();
    }

    #move(e) {
        const p = this.#local(e);
        const drag = this.#drag;
        if (!drag || drag.id !== e.pointerId) {
            this.viewport.pointerMoved(p.x, p.y);
            return;
        }
        const dx = p.x - drag.x0;
        const dy = p.y - drag.y0;
        if (!drag.moved && Math.hypot(dx, dy) < DRAG_THRESHOLD) {
            return;
        }
        if (!drag.moved) {
            drag.moved = true;
            this.viewport.pointerDragging();
            if (drag.button !== 0) {
                this.app.releaseFollow();
            }
        }
        const aspect = p.w / p.h;
        let next;
        if (drag.button === 0) {
            next = orbitDrag(drag.camera, dx, dy);
        } else if (drag.shift) {
            next = panVertical(drag.camera, dy, p.h);
        } else {
            next = panGrab(drag.camera, screenToNdc(drag.x0, drag.y0, p.w, p.h), screenToNdc(p.x, p.y, p.w, p.h), aspect);
        }
        this.app.setCamera(next);
    }

    #up(e, cancelled = false) {
        const drag = this.#drag;
        if (!drag || drag.id !== e.pointerId) {
            return;
        }
        this.#drag = null;
        if (this.viewport.canvas.hasPointerCapture(e.pointerId)) {
            this.viewport.canvas.releasePointerCapture(e.pointerId);
        }
        if (!cancelled && !drag.moved && drag.button === 0) {
            const p = this.#local(e);
            this.viewport.click(p.x, p.y, e);
        }
        this.app.endInteraction();
    }

    #doubleClick(e) {
        const p = this.#local(e);
        const hit = this.viewport.pickWorld(p.x, p.y);
        if (hit && !hit.estimated) {
            const camera = this.app.view.camera;
            this.app.setCamera(lookAtPoint(camera, hit.point, Math.max(Math.min(camera.distance, 60), 8)), { immediate: true });
        }
    }

    #wheel(e) {
        e.preventDefault();
        const p = this.#local(e);
        const delta = e.deltaMode === 1 ? e.deltaY * 33 : e.deltaMode === 2 ? e.deltaY * 400 : e.deltaY;
        const factor = clamp(Math.exp(delta * 0.0016), 0.6, 1.6);
        const camera = this.app.view.camera;
        const hit = this.viewport.pickWorld(p.x, p.y);
        const point = camera.mode === 'top' ? [hit.point[0], hit.point[1], camera.target[2]] : hit.point;
        this.app.beginInteraction();
        this.app.setCamera(zoomAt(camera, factor, point));
        clearTimeout(this.#wheelTimer);
        this.#wheelTimer = setTimeout(() => {
            if (!this.#drag && !this.#keyFlight) {
                this.app.endInteraction();
            }
        }, 220);
    }

    #keyDown(e) {
        this.#shift = e.shiftKey;
        if (e.ctrlKey || e.metaKey || e.altKey || isTypingTarget(e.target) || document.body.classList.contains('modal-open')) {
            return;
        }
        const key = e.key.toLowerCase();
        if (!MOVE_KEYS.has(key)) {
            return;
        }
        e.preventDefault();
        if (!this.#keys.has(key)) {
            this.#keys.add(key);
        }
        if (!this.#keyFlight) {
            this.#keyFlight = true;
            this.app.beginInteraction();
            this.app.releaseFollow();
        }
        this.viewport.requestRender();
    }

    #keyUp(e) {
        this.#shift = e.shiftKey;
        this.#keys.delete(e.key.toLowerCase());
        if (this.#keys.size === 0) {
            this.#endFlight();
        }
    }

    #releaseKeys() {
        this.#keys.clear();
        this.#endFlight();
        if (this.#drag) {
            this.#drag = null;
            this.app.endInteraction();
        }
    }

    #endFlight() {
        if (this.#keyFlight) {
            this.#keyFlight = false;
            this.app.endInteraction();
        }
    }

    /** True while movement keys are held (the render loop keeps running). */
    get flying() {
        return this.#keys.size > 0;
    }

    /** Applies held movement keys for `dt` seconds. Returns true when the camera moved. */
    tick(dt) {
        if (this.#keys.size === 0) {
            return false;
        }
        const k = this.#keys;
        const forward = (k.has('w') ? 1 : 0) - (k.has('s') ? 1 : 0);
        const right = (k.has('d') ? 1 : 0) - (k.has('a') ? 1 : 0);
        const up = (k.has('e') ? 1 : 0) - (k.has('q') ? 1 : 0);
        if (forward === 0 && right === 0 && up === 0) {
            return false;
        }
        const camera = this.app.view.camera;
        const boost = this.#shift ? 4 : 1;
        const speed = (camera.mode === 'fly' ? 40 : Math.max(camera.distance * 0.9, 4)) * boost;
        this.app.setCamera(moveCamera(camera, forward, right, up, speed, Math.min(dt, 0.1)));
        return true;
    }
}

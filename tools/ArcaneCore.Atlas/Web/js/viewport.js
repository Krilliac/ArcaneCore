// The map viewport: owns the canvases, the renderer, the tile scene and the render loop; turns app state into frames,
// answers cursor picks (GPU triangle ids, then an exact ray/plane hit), draws labels and pins, and produces captures.

import { eye as cameraEye, frameBounds, intersectPlaneZ, lookAtPoint, ndcToScreen, project, rayThrough, screenToNdc, vec } from './camera.js';
import { LabelLayer, drawLabels, layoutLabels, LABEL_FONT } from './labels.js';
import { buildLiveGeometry, pickEntity } from './live.js';
import { describeSurface, normalizePick } from './normalize.js';
import { computeNotices } from './notices.js';
import { Renderer, RendererUnavailable } from './renderer.js';
import { Scene } from './scene.js';
import { TextureStore } from './materials/textures.js';
import { wantedTileLayers } from './layerstate.js';
import { Navigator } from './nav.js';
import { tileOf } from './tiles.js';
import { Hud } from './ui/hud.js';
import { clamp, h, mergePatch } from './util.js';
import { normalizeView } from './view.js';

const BACKGROUND = [0x10 / 255, 0x1a / 255, 0x21 / 255];
const MAX_DPR = 2;
const PEEK_DEBOUNCE_MS = 300;
const CAPTURE_MIN = 16;
const MIB = 1024 * 1024;
// Frames under 80 % of the texture budget before a radius given up to an over-budget working set is taken back.
const TEXTURE_RECOVERY_FRAMES = 120;
const isTexturedLayer = (id) => id.endsWith('.tex');

export class Viewport {
    constructor(app, root) {
        this.app = app;
        this.root = root;
        this.canvas = h('canvas', { class: 'gl-canvas', aria: { hidden: 'true' } });
        this.labelCanvas = h('canvas', { class: 'label-canvas', aria: { hidden: 'true' } });
        root.append(this.canvas, this.labelCanvas);
        this.labels = new LabelLayer(this.labelCanvas);
        this.hud = new Hud(root, {
            onCompass: () => this.#faceNorth(),
            onDivider: (fraction, final) => this.app.patchView({ split: fraction }, { immediate: final }),
        });
        this.renderer = null;
        this.scene = null;
        this.nav = new Navigator({ app, viewport: this });
        this.cssWidth = 1;
        this.cssHeight = 1;
        this.dpr = 1;
        this.dirty = true;
        this.lastTime = performance.now();
        this.drawTimes = [];
        this.statsAt = 0;
        this.hoverInfo = null;
        this.pendingHover = null;
        this.hoverAt = 0;
        this.liveDirty = true;
        this.liveSets = null;
        this.liveLabels = [];
        this.liveEntities = [];
        this.lastLiveBuild = 0;
        this.syncKey = '';
        this.peekTimer = null;
        this.peekAbort = null;
        this.retryTimer = null;
        this.depthHint = 100;
        this.liveAnimated = false;
        this.liveCounts = { entities: 0, splines: 0, routes: 0 };
        this.textureStore = null;
        this.textureBackoff = 0;
        this.textureRecovery = 0;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Setup

    init() {
        const app = this.app;
        try {
            this.renderer = new Renderer(this.canvas, {
                onContextLost: () => {
                    this.scene?.reset();
                    app.toast('The GPU context was lost. The map reloads when it comes back.', 'warn');
                },
                onContextRestored: () => {
                    this.#createTextures();
                    this.scene?.reset();
                    this.#dropOverlaySets();
                    this.liveSets = null;
                    this.liveDirty = true;
                    this.syncKey = '';
                    this.#syncScene();
                    this.dirty = true;
                },
            });
            app.webgl = { ok: true, message: '' };
            this.#createTextures();
            this.scene = new Scene({ api: app.api, renderer: this.renderer });
            this.scene.setLayerInfos(app.layers);
            app.scene = this.scene;
            this.scene.on('change', () => {
                this.dirty = true;
                app.events.emit('tiles');
            });
        } catch (error) {
            const message = error instanceof RendererUnavailable ? error.message : `The renderer failed to start: ${error.message}`;
            app.webgl = { ok: false, message };
        }

        new ResizeObserver(() => this.#resize()).observe(this.root);
        this.#resize();
        this.nav.attach();

        app.events.on('view', (view, changed) => {
            this.dirty = true;
            if (changed.has('textureBudgetMb')) {
                this.textureStore?.setBudget(view.textureBudgetMb * MIB);
            }
            if (changed.has('textureRadius')) {
                this.textureBackoff = 0;
                this.textureRecovery = 0;
            }
            if (changed.has('layers') || changed.has('follow') || changed.has('compare') || changed.has('map')) {
                this.liveDirty = true;
            }
            this.#syncScene();
        });
        app.events.on('layers', () => {
            this.scene?.setLayerInfos(app.layers);
            this.syncKey = '';
            this.#syncScene();
            this.dirty = true;
        });
        app.events.on('overlays', () => (this.dirty = true));
        app.events.on('selection', () => {
            this.liveDirty = true;
            this.dirty = true;
        });
        app.events.on('hover', () => (this.dirty = true));
        app.events.on('connection', () => (this.dirty = true));
        app.events.on('status', () => (this.dirty = true));
        app.events.on('tiles', () => (this.dirty = true));
        app.live.on('change', () => {
            this.liveDirty = true;
            this.dirty = true;
        });
        app.overlays.on('remove', (entry) => this.#disposeEntry(entry));
        this.retryTimer = setInterval(() => this.#syncScene(true), 3000);
        document.addEventListener('visibilitychange', () => (this.dirty = true));
        this.#syncScene();
        this.raf = requestAnimationFrame((t) => this.#tick(t));
    }

    /** A fresh texture store on the renderer's current GL context (at start and after a context restore). */
    #createTextures() {
        this.textureStore?.dispose();
        const allowDxt = new URLSearchParams(window.location.search).get('texfmt') !== 'rgba';
        this.textureStore = new TextureStore({
            gl: this.renderer.gl,
            fetchTexture: (key, options) => this.app.api.texture(key, options),
            budgetBytes: this.app.view.textureBudgetMb * MIB,
            allowDxt,
            onChange: () => (this.dirty = true),
        });
        this.renderer.setTextureStore(this.textureStore);
    }

    /**
     * Anti-thrash (design section 4): while the textures in use do not fit the budget the textured layers load one tile ring fewer;
     * the ring comes back after TEXTURE_RECOVERY_FRAMES frames under 80 % of the budget.
     */
    #adaptTextures() {
        const stats = this.textureStore?.stats();
        if (!stats) {
            return;
        }
        const wanted = this.app.view.textureRadius;
        if (stats.overBudget) {
            this.textureRecovery = 0;
            if (this.textureBackoff < wanted) {
                this.textureBackoff++;
                this.#syncScene();
            }
        } else if (this.textureBackoff > 0 && stats.bytes < 0.8 * stats.budgetBytes) {
            if (++this.textureRecovery >= TEXTURE_RECOVERY_FRAMES) {
                this.textureRecovery = 0;
                this.textureBackoff--;
                this.#syncScene();
            }
        } else {
            this.textureRecovery = 0;
        }
    }

    #resize() {
        const rect = this.root.getBoundingClientRect();
        this.cssWidth = Math.max(1, rect.width);
        this.cssHeight = Math.max(1, rect.height);
        this.dpr = Math.min(window.devicePixelRatio || 1, MAX_DPR);
        this.renderer?.resize(this.cssWidth * this.dpr, this.cssHeight * this.dpr);
        this.labels.resize(this.cssWidth, this.cssHeight, this.dpr);
        this.dirty = true;
    }

    get pixelScale() {
        return this.dpr;
    }

    requestRender() {
        this.dirty = true;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Scene window

    #focusPoint() {
        const camera = this.app.view.camera;
        return camera.mode === 'fly' ? cameraEye(camera) : camera.target;
    }

    #syncScene(force = false) {
        const app = this.app;
        if (!this.scene) {
            return;
        }
        const view = app.view;
        const focus = this.#focusPoint();
        const tile = tileOf(focus[0], focus[1]);
        const layers = wantedTileLayers(app.resolved());
        const textureRadius = Math.max(0, view.textureRadius - this.textureBackoff);
        const key = `${view.map}|${tile ? `${tile.x},${tile.y}` : 'none'}|${view.loadRadius}|${layers.join(',')}|${textureRadius}`;
        if (key === this.syncKey && !force) {
            return;
        }
        const tileChanged = key.split('|').slice(0, 3).join('|') !== this.syncKey.split('|').slice(0, 3).join('|');
        this.syncKey = key;
        const layerRadius = Object.fromEntries(layers.filter(isTexturedLayer).map((id) => [id, textureRadius]));
        this.scene.update({ map: view.map, focusTile: tile, radius: view.loadRadius, layers, layerRadius });
        if (tileChanged || force) {
            this.#schedulePeek();
        }
    }

    #schedulePeek() {
        clearTimeout(this.peekTimer);
        this.peekTimer = setTimeout(() => this.#peek(), PEEK_DEBOUNCE_MS);
    }

    async #peek() {
        const app = this.app;
        const scene = this.scene;
        const view = app.view;
        const tile = tileOf(...this.#focusPoint().slice(0, 2));
        if (!scene || !tile) {
            return;
        }
        this.peekAbort?.abort();
        const abort = new AbortController();
        this.peekAbort = abort;
        const layers = app.layers.filter((l) => l.tiled !== false && l.available !== false && !l.id.startsWith('live.')).map((l) => l.id);
        let next = 0;
        const worker = async () => {
            while (next < layers.length && !abort.signal.aborted) {
                const layer = layers[next++];
                try {
                    const answer = await app.api.tiles(layer, view.map, tile.x, tile.y, view.loadRadius, abort.signal);
                    if (!abort.signal.aborted && app.view.map === view.map) {
                        scene.mergePeek(layer, answer);
                    }
                } catch (error) {
                    if (error?.name === 'AbortError') {
                        return;
                    }
                    if (error?.status === 404 || error?.status === 400) {
                        continue;
                    }
                    return;
                }
            }
        };
        await Promise.all([worker(), worker(), worker(), worker()]);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Frames

    /** Layers in draw order with their loaded chunks. */
    #layerEntries() {
        const out = [];
        if (!this.scene) {
            return out;
        }
        for (const l of this.app.resolved()) {
            if (!l.visible || l.live) {
                continue;
            }
            const chunks = this.scene.chunksFor(l.id);
            if (chunks.length === 0) {
                continue;
            }
            out.push({
                id: l.id,
                chunks,
                regions: l.regions,
                color: l.style.color,
                opacity: l.opacity,
                mode: l.mode,
                byAttribute: l.style.colorByAttribute,
            });
        }
        return out;
    }

    #setsFor() {
        const sets = [];
        for (const entry of this.app.overlays.entries()) {
            if (!entry.visible || !this.renderer) {
                continue;
            }
            if (!entry.gpu) {
                const g = entry.geometry;
                entry.gpu = {
                    solid: g.solid ? this.renderer.createLineSet(g.solid) : null,
                    dashed: g.dashed ? this.renderer.createLineSet(g.dashed) : null,
                    markers: g.markers ? this.renderer.createMarkerSet(g.markers) : null,
                };
            }
            if (entry.gpu.solid) {
                sets.push({ set: entry.gpu.solid, width: 3, alpha: 1, ghost: true });
            }
            if (entry.gpu.dashed) {
                sets.push({ set: entry.gpu.dashed, width: 3, alpha: 1, dashed: true, ghost: true });
            }
            if (entry.gpu.markers) {
                sets.push({ set: entry.gpu.markers, alpha: 1, ghost: true, scale: 1 });
            }
        }
        if (this.liveSets) {
            if (this.liveSets.solid) {
                sets.push({ set: this.liveSets.solid, width: 2.25, alpha: 1, ghost: true });
            }
            if (this.liveSets.dashed) {
                sets.push({ set: this.liveSets.dashed, width: 2.25, alpha: 1, dashed: true, ghost: true });
            }
            if (this.liveSets.markers) {
                sets.push({ set: this.liveSets.markers, alpha: 1, ghost: true, scale: 1 });
            }
        }
        return sets;
    }

    /** Builds the renderer's frame description for any target size. */
    buildFrame({ width, height, pixelScale, view = this.app.view, withSets = true }) {
        const split = view.compare === 'split' ? Math.round(view.split * width) : null;
        return {
            camera: view.camera,
            width,
            height,
            aspect: width / height,
            pixelScale,
            background: BACKGROUND,
            split,
            layers: this.#layerEntries(),
            sets: withSets ? this.#setsFor() : [],
        };
    }

    #screenFrame() {
        return this.buildFrame({
            width: this.canvas.width,
            height: this.canvas.height,
            pixelScale: this.dpr,
            withSets: false,
        });
    }

    // ------------------------------------------------------------------------------------------------------------
    // Loop

    #tick(time) {
        this.raf = requestAnimationFrame((t) => this.#tick(t));
        const dt = Math.min((time - this.lastTime) / 1000, 0.1);
        this.lastTime = time;
        let animating = false;
        if (this.nav.tick(dt) || this.nav.flying) {
            animating = true;
        }
        if (this.#follow()) {
            animating = true;
        }
        if (this.scene?.pump(5)) {
            animating = true;
        }
        if (Math.abs((window.devicePixelRatio > MAX_DPR ? MAX_DPR : window.devicePixelRatio || 1) - this.dpr) > 0.01) {
            this.#resize();
        }
        if (this.pendingHover && this.renderer && !this.renderer.lost) {
            this.#doHover();
        }
        const liveAnimating = this.#liveNeedsFrame(time);
        if (!(this.dirty || animating || liveAnimating)) {
            this.#publishStats(time, false);
            return;
        }
        this.dirty = false;
        this.#draw(time);
        this.#publishStats(time, true);
    }

    /** Spline heads and trail fades move with the clock, so those layers rebuild about 20 times a second. */
    #liveNeedsFrame(time) {
        return this.liveAnimated && time - this.lastLiveBuild > 50;
    }

    #liveStyles() {
        const styles = {};
        for (const l of this.app.resolved()) {
            if (l.live) {
                styles[l.id] = { visible: l.visible, color: l.style.color, opacity: l.opacity, colorByAttribute: l.style.colorByAttribute, attributeColors: l.info.attributeColors };
            }
        }
        return styles;
    }

    #rebuildLive(time) {
        const app = this.app;
        const styles = this.#liveStyles();
        const geometry = buildLiveGeometry(app.live, app.view.map, styles, time, app.selection.entityGuid, app.view.follow);
        for (const set of Object.values(this.liveSets ?? {})) {
            this.renderer?.destroySet(set);
        }
        this.liveSets = this.renderer
            ? {
                solid: geometry.solid ? this.renderer.createLineSet(geometry.solid) : null,
                dashed: geometry.dashed ? this.renderer.createLineSet(geometry.dashed) : null,
                markers: geometry.markers ? this.renderer.createMarkerSet(geometry.markers) : null,
            }
            : null;
        this.liveLabels = geometry.labelCandidates;
        this.liveEntities = geometry.frame ? [...geometry.frame.entities.values()].filter((e) => styles[layerOfKind(e.kind)]?.visible) : [];
        this.liveCounts = geometry.counts;
        this.liveAnimated = geometry.counts.splines > 0 || styles['live.trails']?.visible === true;
        this.lastLiveBuild = time;
        this.liveDirty = false;
    }

    #draw(time) {
        const app = this.app;
        const view = app.view;
        if (this.liveDirty || this.#liveNeedsFrame(time)) {
            this.#rebuildLive(time);
        }
        if (this.renderer && !this.renderer.lost) {
            const frame = this.buildFrame({ width: this.canvas.width, height: this.canvas.height, pixelScale: this.dpr });
            this.renderer.render(frame);
            app.stats.drawCalls = this.renderer.stats.drawCalls;
            app.stats.triangles = this.renderer.stats.triangles;
            app.stats.lines = this.renderer.stats.lines;
            app.stats.textures = this.textureStore?.stats() ?? app.stats.textures;
            this.#adaptTextures();
        }
        this.#drawLabels(view);
        this.hud.updateView(view, this.cssWidth, this.cssHeight, this.depthHint);
        this.#updateReadout();
        this.#updateNotices();
        this.drawTimes.push(time);
    }

    #labelCandidates() {
        const candidates = [...this.liveLabels];
        for (const entry of this.app.overlays.entries()) {
            if (entry.visible) {
                for (const l of entry.geometry.labels) {
                    candidates.push({ at: l.at, text: l.text, color: l.color, priority: 4 });
                }
            }
        }
        return candidates;
    }

    #drawLabels(view) {
        const ctx = this.labels.ctx;
        this.labels.draw(this.#labelCandidates(), view.camera);
        // Pins: the selected point (cobalt) and the point under the cursor (white). Drawn in 2D, so they never hide behind terrain.
        ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
        const sel = this.app.selection.point;
        if (sel) {
            this.#pin(ctx, sel, '#4F8BFF', 9, true);
        }
        const hov = this.hoverInfo;
        if (hov && !this.nav.flying) {
            this.#pin(ctx, hov.point, hov.estimated ? 'rgba(230,236,239,0.45)' : 'rgba(230,236,239,0.9)', 5, false);
        }
    }

    #pin(ctx, point, color, radius, cross) {
        const p = project(this.app.view.camera, point, this.cssWidth / this.cssHeight);
        if (!p) {
            return;
        }
        const s = ndcToScreen(p.x, p.y, this.cssWidth, this.cssHeight);
        ctx.save();
        ctx.strokeStyle = 'rgba(8,13,17,0.9)';
        ctx.lineWidth = 3.5;
        ctx.beginPath();
        ctx.arc(s.x, s.y, radius, 0, Math.PI * 2);
        ctx.stroke();
        ctx.strokeStyle = color;
        ctx.lineWidth = 1.75;
        ctx.stroke();
        if (cross) {
            ctx.beginPath();
            ctx.moveTo(s.x - radius - 5, s.y);
            ctx.lineTo(s.x - radius + 2, s.y);
            ctx.moveTo(s.x + radius - 2, s.y);
            ctx.lineTo(s.x + radius + 5, s.y);
            ctx.moveTo(s.x, s.y - radius - 5);
            ctx.lineTo(s.x, s.y - radius + 2);
            ctx.moveTo(s.x, s.y + radius - 2);
            ctx.lineTo(s.x, s.y + radius + 5);
            ctx.stroke();
        }
        ctx.restore();
    }

    #updateReadout() {
        const app = this.app;
        const hover = this.hoverInfo;
        this.hud.updateReadout(
            hover ? { point: hover.point, estimated: hover.estimated, surface: hover.surface } : null,
            app.view.map,
            (layer, x, y) => this.scene?.statusOf(layer, x, y) ?? null,
        );
    }

    #updateNotices() {
        const app = this.app;
        const tile = tileOf(...this.#focusPoint().slice(0, 2));
        this.hud.setNotices(computeNotices({
            webgl: app.webgl,
            connection: app.connection,
            status: app.status,
            resolved: app.resolved(),
            map: app.view.map,
            focusTile: tile,
            statusOf: (layer, x, y) => this.scene?.statusOf(layer, x, y) ?? null,
        }));
    }

    #publishStats(time, drew) {
        if (time - this.statsAt < 500) {
            return;
        }
        this.statsAt = time;
        this.drawTimes = this.drawTimes.filter((t) => time - t <= 1000);
        const fps = drew || this.drawTimes.length > 0 ? this.drawTimes.length : 0;
        this.app.stats.fps = fps;
        if (this.scene) {
            const resolved = this.app.resolved();
            const ids = resolved.filter((l) => l.visible && !l.live).map((l) => l.id);
            this.app.stats.tiles = this.scene.totals(ids);
        }
        this.app.stats.gpuBytes = this.renderer?.stats.gpuBytes ?? 0;
        if (this.textureStore) {
            this.app.stats.textures = this.textureStore.stats();
        }
        this.app.events.emit('stats', this.app.stats);
    }

    #follow() {
        const app = this.app;
        const guid = app.view.follow;
        if (!guid || app.interacting) {
            return false;
        }
        const found = app.live.find(guid);
        if (!found || found.frame.map !== app.view.map) {
            return false;
        }
        const camera = app.view.camera;
        // Orbit and top: the target glides onto the entity. Fly: the eye holds 35 yd behind it along the look direction.
        const goal = camera.mode === 'fly' ? lookAtPoint(camera, found.entity.pos, 35).target : found.entity.pos;
        const d = vec.sub(goal, camera.target);
        if (vec.length(d) < 0.02) {
            return false;
        }
        app.setCamera({ ...camera, target: vec.add(camera.target, vec.scale(d, 0.18)) });
        return true;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Picking and pointer

    pointerMoved(x, y) {
        this.pendingHover = { x, y };
        this.hoverAt = performance.now();
    }

    pointerLeft() {
        this.pendingHover = null;
        this.hoverInfo = null;
        this.app.setHover(null);
        this.dirty = true;
    }

    pointerDragging() {
        this.pendingHover = null;
    }

    #doHover() {
        const { x, y } = this.pendingHover;
        this.pendingHover = null;
        this.hoverInfo = this.pickWorld(x, y);
        this.depthHint = this.hoverInfo && !this.hoverInfo.estimated
            ? vec.length(vec.sub(this.hoverInfo.point, cameraEye(this.app.view.camera)))
            : this.depthHint;
        this.app.setHover(this.hoverInfo);
        this.dirty = true;
    }

    /**
     * The world point under a viewport pixel (CSS px): the exact point on the visible surface when a triangle is there,
     * else the point on the horizontal plane through the camera target (`estimated`).
     */
    pickWorld(x, y) {
        const app = this.app;
        const camera = app.view.camera;
        const ndc = screenToNdc(x, y, this.cssWidth, this.cssHeight);
        const ray = rayThrough(camera, app.view.map, ndc.x, ndc.y, this.cssWidth / this.cssHeight, 1e6);
        if (this.renderer && !this.renderer.lost && this.scene) {
            const hit = this.renderer.pick(this.#screenFrame(), x * this.dpr, y * this.dpr);
            if (hit) {
                const tri = this.renderer.readTriangle(hit.chunk, hit.triangle);
                if (tri) {
                    const point = rayTrianglePlane(ray, tri.points);
                    if (point) {
                        const title = app.layerInfo(hit.chunk.layerId)?.title ?? hit.chunk.layerId;
                        return {
                            point,
                            estimated: false,
                            surface: describeSurface(title, hit.chunk.attributes.get(tri.attribute), tri.attribute),
                            layerId: hit.chunk.layerId,
                            tile: hit.chunk.tile,
                            attribute: tri.attribute,
                        };
                    }
                }
            }
        }
        const plane = intersectPlaneZ(ray, camera.target[2]);
        const fallback = plane ?? vec.add(ray.origin, vec.scale(ray.direction, camera.distance));
        return { point: fallback, estimated: true, surface: '', layerId: null, tile: null, attribute: null };
    }

    async click(x, y) {
        const app = this.app;
        const camera = app.view.camera;
        const aspect = this.cssWidth / this.cssHeight;
        const entity = pickEntity(this.liveEntities, (p) => {
            const n = project(camera, p, aspect);
            return n ? ndcToScreen(n.x, n.y, this.cssWidth, this.cssHeight) : null;
        }, x, y, 12);
        if (entity) {
            app.recordClick(entity.pos);
            app.select({ entityGuid: entity.guid, point: entity.pos, tile: tileOf(entity.pos[0], entity.pos[1]) });
            app.events.emit('focus-tab', 'live');
            return;
        }
        const world = this.pickWorld(x, y);
        app.recordClick(world.point);
        app.select({ point: world.point, hit: null, tile: tileOf(world.point[0], world.point[1]), pending: true });
        app.events.emit('focus-tab', 'inspector');
        await this.#serverPick(x, y, world);
    }

    async #serverPick(x, y, world) {
        const app = this.app;
        const pickCommand = app.commandNamed('query.pick');
        if (!pickCommand) {
            app.select({ point: world.point, hit: null, tile: tileOf(world.point[0], world.point[1]), pending: false, note: 'The host has no query.pick command.' });
            return;
        }
        const camera = app.view.camera;
        const ndc = screenToNdc(x, y, this.cssWidth, this.cssHeight);
        const ray = rayThrough(camera, app.view.map, ndc.x, ndc.y, this.cssWidth / this.cssHeight);
        const region = app.view.compare === 'split' ? (x < app.view.split * this.cssWidth ? 'left' : 'right') : 'full';
        const layers = app.resolved().filter((l) => l.visible && !l.live && l.regions.includes(region)).map((l) => l.id);
        const props = pickCommand.schema.properties ?? {};
        const args = {};
        if ('ray' in props) {
            args.ray = { origin: ray.origin, direction: ray.direction };
        } else if ('ndc' in props) {
            args.ndc = [ndc.x, ndc.y];
        }
        if ('layers' in props) {
            args.layers = layers;
        }
        const result = await app.runCommand('query.pick', args, { quiet: true });
        const sameClick = app.selection.point === world.point;
        if (!sameClick) {
            return;
        }
        if (!result.ok) {
            app.select({ point: world.point, hit: null, tile: tileOf(world.point[0], world.point[1]), pending: false, note: result.error?.message ?? 'query.pick failed' });
            return;
        }
        const pick = normalizePick(result.data);
        app.select({
            point: pick.hit?.point ?? world.point,
            hit: pick.hit,
            all: pick.all,
            tile: pick.hit?.tile ?? tileOf(world.point[0], world.point[1]),
            pending: false,
            note: pick.hit ? '' : 'The host found nothing within reach of that ray on the visible layers.',
        });
    }

    #faceNorth() {
        const camera = this.app.view.camera;
        this.app.setCamera({ ...camera, yaw: 0 }, { immediate: true });
    }

    // ------------------------------------------------------------------------------------------------------------
    // Framing

    /** F: frames the followed entity, else the selection, else everything loaded (shift: always everything loaded). */
    frameSelection({ all = false } = {}) {
        const app = this.app;
        const camera = app.view.camera;
        if (!all) {
            const guid = app.view.follow ?? app.selection.entityGuid;
            const found = guid ? app.live.find(guid) : null;
            if (found) {
                app.setCamera(lookAtPoint(camera, found.entity.pos, 35), { immediate: true });
                return true;
            }
            if (app.selection.point) {
                app.setCamera(lookAtPoint(camera, app.selection.point, 40), { immediate: true });
                return true;
            }
        }
        const bounds = this.scene?.loadedBounds();
        if (bounds) {
            app.setCamera(frameBounds(camera, bounds.min, bounds.max, this.cssWidth / this.cssHeight), { immediate: true });
            return true;
        }
        app.toast('Nothing is loaded to frame yet. Tick a layer and wait for its tiles.', 'info');
        return false;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Capture

    /**
     * Renders the current view (optionally with a merge patch applied to a copy) off-screen at width x height and returns
     * a PNG blob. Waits (up to 8 s) for tiles that are still loading so the image shows what is wanted, not a half-loaded map.
     */
    async capturePng({ width, height, view: patch }) {
        const app = this.app;
        if (!this.renderer || this.renderer.lost) {
            throw new Error('The GPU renderer is not available in this browser.');
        }
        if (!Number.isInteger(width) || !Number.isInteger(height) || width < CAPTURE_MIN || height < CAPTURE_MIN) {
            throw new RangeError(`The capture size must be whole pixels, at least ${CAPTURE_MIN} per side.`);
        }
        if (this.scene) {
            await this.scene.idle(8000);
            while (this.scene.pump(1000)) {
                // pump until every decoded tile is on the GPU
            }
        }
        let view = app.view;
        if (patch && typeof patch === 'object') {
            view = normalizeView(mergePatch(app.view, patch), app.view);
        }
        const scale = clamp(height / 900, 1, 3);
        const frame = this.buildFrame({ width, height, pixelScale: scale, view });
        const pixels = this.renderer.renderToPixels(frame);
        const canvas = document.createElement('canvas');
        canvas.width = width;
        canvas.height = height;
        const ctx = canvas.getContext('2d');
        ctx.putImageData(new ImageData(pixels.data, width, height), 0, 0);
        ctx.font = `${11 * scale}px ${LABEL_FONT}`;
        const labels = layoutLabels(this.#labelCandidates(), view.camera, width, height, (t) => ctx.measureText(t).width, 120, 14 * scale);
        drawLabels(ctx, labels, scale);
        const blob = await new Promise((resolve) => canvas.toBlob(resolve, 'image/png'));
        if (!blob) {
            throw new Error('The browser could not encode the PNG.');
        }
        this.dirty = true;
        return blob;
    }

    async handleCaptureRequest(message) {
        const app = this.app;
        try {
            const blob = await this.capturePng({ width: message.width, height: message.height, view: message.view });
            await app.api.postCapture(message.id, blob);
        } catch (error) {
            app.socket.sendCaptureFailed(message.id, error.message);
            app.toast(`Capture failed: ${error.message}`, 'error');
        }
    }

    // ------------------------------------------------------------------------------------------------------------

    #disposeEntry(entry) {
        if (entry.gpu && this.renderer) {
            for (const set of Object.values(entry.gpu)) {
                this.renderer.destroySet(set);
            }
        }
        entry.gpu = null;
        this.dirty = true;
    }

    #dropOverlaySets() {
        for (const entry of this.app.overlays.entries()) {
            entry.gpu = null;
        }
    }

    dispose() {
        cancelAnimationFrame(this.raf);
        clearInterval(this.retryTimer);
        clearTimeout(this.peekTimer);
        this.nav.detach();
        this.renderer?.dispose();
    }
}

function layerOfKind(kind) {
    return kind === 'player' ? 'live.players' : kind === 'bot' ? 'live.bots' : kind === 'gameobject' ? 'live.gameobjects' : 'live.creatures';
}

/** Where a ray meets the plane of a triangle (the exact surface point for the triangle the GPU reported). */
export function rayTrianglePlane(ray, points) {
    const [a, b, c] = points;
    const n = vec.cross(vec.sub(b, a), vec.sub(c, a));
    const denom = vec.dot(n, ray.direction);
    if (Math.abs(denom) < 1e-12) {
        return vec.scale(vec.add(vec.add(a, b), c), 1 / 3);
    }
    const t = vec.dot(n, vec.sub(a, ray.origin)) / denom;
    return vec.add(ray.origin, vec.scale(ray.direction, t));
}


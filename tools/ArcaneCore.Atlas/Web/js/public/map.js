// The public page's map: the model of what a frame says, the camera, hit testing and the 2D drawing. It reads only the
// fields the guest projection writes (opaque id `g`, kind `k`, entry `e`, name `n`, position `x y z`, facing `o`, health
// `hp`, motion `mt`, and the `playerCells` list). Anything else in a frame is ignored, so nothing the server did not
// project can reach the screen. Coordinates are world yards: +x is north, +y is west, so the chart puts north up and
// east (negative y) to the right.

import { clamp } from '../util.js';

export const KINDS = ['character', 'creature', 'gameobject'];

/** The layer each entity kind belongs to (the ids `guest.layers` lists). */
export const KIND_LAYER = { character: 'live.players', creature: 'live.creatures', gameobject: 'live.gameobjects' };

/** Used when `guest.layers` has not answered (yet). */
export const FALLBACK_LAYERS = [
    { id: 'live.players', title: 'Characters', defaultColor: '#7FD3FF', defaultVisible: true },
    { id: 'live.creatures', title: 'Creatures', defaultColor: '#C9D1D6', defaultVisible: true },
    { id: 'live.gameobjects', title: 'Objects', defaultColor: '#A3B1BA', defaultVisible: true },
];

const MOTION_COLORS = { idle: '#C9D1D6', moving: '#FFD34D', chase: '#FF5A5A', follow: '#3CCB9A' };
const MOTIONS = new Set(['idle', 'moving', 'chase', 'follow']);
const ID_PATTERN = /^[0-9a-f]{16}$/;
const MAX_ENTITIES = 5000;
const MAX_CELLS = 2000;
const TILE_YARDS = 533.33333;
export const MIN_SCALE = 0.008;
export const MAX_SCALE = 8;

const isObject = (value) => value !== null && typeof value === 'object' && !Array.isArray(value);
const finite = (value) => typeof value === 'number' && Number.isFinite(value);

function readEntity(raw) {
    if (!isObject(raw) || typeof raw.g !== 'string' || !ID_PATTERN.test(raw.g) || !KINDS.includes(raw.k) || !finite(raw.x) || !finite(raw.y)) {
        return null;
    }
    return {
        id: raw.g,
        kind: raw.k,
        entry: Number.isInteger(raw.e) ? raw.e : 0,
        name: typeof raw.n === 'string' ? raw.n.slice(0, 64) : '',
        x: raw.x,
        y: raw.y,
        z: finite(raw.z) ? raw.z : 0,
        o: finite(raw.o) ? raw.o : 0,
        hp: finite(raw.hp) ? raw.hp : null,
        motion: MOTIONS.has(raw.mt) ? raw.mt : null,
    };
}

function readCell(raw) {
    return isObject(raw) && finite(raw.x) && finite(raw.y) && Number.isInteger(raw.n) && raw.n > 0 ? { x: raw.x, y: raw.y, n: raw.n } : null;
}

/** What the latest frame of the viewed map says. */
export class MapModel {
    constructor() {
        this.reset(null);
    }

    reset(map) {
        this.map = map;
        this.entities = new Map();
        this.cells = [];
        this.cellYards = 250;
        this.seq = -1;
        this.reduced = false;
        this.hasFrame = false;
        this.connected = false;
        this.detail = 'waiting';
    }

    /** Replaces the entities and cells with a `live.frame` event's data; false when the data is not a frame of the viewed map. */
    applyFrame(data) {
        if (!isObject(data) || !finite(data.seq) || (this.map !== null && data.map !== this.map)) {
            return false;
        }
        const entities = new Map();
        for (const raw of (Array.isArray(data.entities) ? data.entities : []).slice(0, MAX_ENTITIES)) {
            const entity = readEntity(raw);
            if (entity) {
                entities.set(entity.id, entity);
            }
        }
        this.entities = entities;
        this.cells = (Array.isArray(data.playerCells) ? data.playerCells : []).slice(0, MAX_CELLS).map(readCell).filter(Boolean);
        this.cellYards = finite(data.cellYards) && data.cellYards >= 10 ? data.cellYards : 250;
        this.seq = data.seq;
        this.reduced = data.reduced === true;
        this.hasFrame = true;
        return true;
    }

    /** Applies a `live.status` event's data. */
    applyStatus(data) {
        if (!isObject(data) || (this.map !== null && data.map !== this.map)) {
            return;
        }
        this.connected = data.connected === true;
        this.detail = typeof data.detail === 'string' ? data.detail : '';
        if (!this.connected && !this.hasFrame) {
            this.entities = new Map();
            this.cells = [];
        }
    }

    counts(layers) {
        const counts = { character: 0, creature: 0, gameobject: 0, cells: this.cells.length };
        for (const entity of this.entities.values()) {
            if (layers.has(KIND_LAYER[entity.kind])) {
                counts[entity.kind]++;
            }
        }
        return counts;
    }
}

/** A top-down camera: `scale` pixels per yard, centred on a world point. */
export class Camera {
    constructor() {
        this.cx = 0;
        this.cy = 0;
        this.scale = 0.03;
    }

    worldToScreen(x, y, width, height) {
        return [width / 2 - ((y - this.cy) * this.scale), height / 2 - ((x - this.cx) * this.scale)];
    }

    screenToWorld(sx, sy, width, height) {
        return [this.cx - ((sy - height / 2) / this.scale), this.cy - ((sx - width / 2) / this.scale)];
    }

    /** Drags the chart by a pixel offset (the content follows the pointer). */
    panByPixels(dx, dy) {
        this.cy += dx / this.scale;
        this.cx += dy / this.scale;
    }

    /** Zooms by `factor` keeping the world point under the pixel fixed. */
    zoomAt(sx, sy, factor, width, height) {
        const [wx, wy] = this.screenToWorld(sx, sy, width, height);
        this.scale = clamp(this.scale * factor, MIN_SCALE, MAX_SCALE);
        this.cx = wx + ((sy - height / 2) / this.scale);
        this.cy = wy + ((sx - width / 2) / this.scale);
    }

    centerOn(x, y) {
        this.cx = x;
        this.cy = y;
    }

    /** Centres on and zooms to a set of points, with a margin; a single point (or none) keeps a readable scale. */
    fit(points, width, height, margin = 40) {
        if (points.length === 0 || width <= 0 || height <= 0) {
            return;
        }
        let minX = Infinity;
        let maxX = -Infinity;
        let minY = Infinity;
        let maxY = -Infinity;
        for (const [x, y] of points) {
            minX = Math.min(minX, x);
            maxX = Math.max(maxX, x);
            minY = Math.min(minY, y);
            maxY = Math.max(maxY, y);
        }
        this.centerOn((minX + maxX) / 2, (minY + maxY) / 2);
        const spanX = Math.max(maxX - minX, 100);
        const spanY = Math.max(maxY - minY, 100);
        const scale = Math.min(Math.max(width - (margin * 2), 1) / spanY, Math.max(height - (margin * 2), 1) / spanX);
        this.scale = clamp(scale, MIN_SCALE, 2);
    }
}

/** The layers that start visible, as a Set of layer ids. */
export function initialLayers(layers) {
    return new Set(layers.filter((layer) => layer.defaultVisible !== false).map((layer) => layer.id));
}

/**
 * The nearest visible entity within `radius` pixels of the point, else the player cell that contains it, else null.
 * Entities win over cells because a cell is a coarse area and an entity is something the visitor aimed at.
 */
export function hitTest(model, camera, layers, sx, sy, width, height, radius = 9) {
    let best = null;
    let bestDistance = radius * radius;
    for (const entity of model.entities.values()) {
        if (!layers.has(KIND_LAYER[entity.kind])) {
            continue;
        }
        const [ex, ey] = camera.worldToScreen(entity.x, entity.y, width, height);
        const distance = ((ex - sx) ** 2) + ((ey - sy) ** 2);
        if (distance <= bestDistance) {
            best = entity;
            bestDistance = distance;
        }
    }
    if (best) {
        return { type: 'entity', entity: best };
    }
    const [wx, wy] = camera.screenToWorld(sx, sy, width, height);
    const half = model.cellYards / 2;
    for (const cell of model.cells) {
        if (Math.abs(cell.x - wx) <= half && Math.abs(cell.y - wy) <= half) {
            return { type: 'cell', cell };
        }
    }
    return null;
}

function layerColors(layers) {
    const colors = {};
    for (const layer of layers) {
        colors[layer.id] = typeof layer.defaultColor === 'string' && /^#[0-9a-fA-F]{6}$/.test(layer.defaultColor) ? layer.defaultColor : '#C9D1D6';
    }
    return colors;
}

function drawGrid(ctx, camera, width, height) {
    let step = TILE_YARDS;
    while (step * camera.scale < 36) {
        step *= 2;
    }
    const [cornerX, cornerY] = camera.screenToWorld(0, 0, width, height);
    const [farX, farY] = camera.screenToWorld(width, height, width, height);
    ctx.lineWidth = 1;
    ctx.strokeStyle = 'rgba(147, 161, 170, 0.14)';
    ctx.beginPath();
    for (let x = Math.ceil(farX / step) * step; x <= cornerX; x += step) {
        const [, sy] = camera.worldToScreen(x, 0, width, height);
        ctx.moveTo(0, sy);
        ctx.lineTo(width, sy);
    }
    for (let y = Math.ceil(farY / step) * step; y <= cornerY; y += step) {
        const [sx] = camera.worldToScreen(0, y, width, height);
        ctx.moveTo(sx, 0);
        ctx.lineTo(sx, height);
    }
    ctx.stroke();
}

/**
 * Draws the chart: background, a tile grid, the coarse player cells, then the entities of the visible layers. Returns how many
 * entities were drawn. `ctx` is a CanvasRenderingContext2D (a recording stand-in in tests).
 */
export function drawMap(ctx, model, camera, layers, layerList, width, height, selectedId = null) {
    ctx.fillStyle = '#101A21';
    ctx.fillRect(0, 0, width, height);
    drawGrid(ctx, camera, width, height);

    const half = (model.cellYards * camera.scale) / 2;
    ctx.font = '12px sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    for (const cell of model.cells) {
        const [sx, sy] = camera.worldToScreen(cell.x, cell.y, width, height);
        if (sx + half < 0 || sy + half < 0 || sx - half > width || sy - half > height) {
            continue;
        }
        ctx.fillStyle = 'rgba(245, 165, 36, 0.20)';
        ctx.fillRect(sx - half, sy - half, half * 2, half * 2);
        ctx.strokeStyle = 'rgba(245, 165, 36, 0.65)';
        ctx.lineWidth = 1;
        ctx.strokeRect(sx - half, sy - half, half * 2, half * 2);
        ctx.fillStyle = '#F5A524';
        ctx.fillText(String(cell.n), sx, sy);
    }

    const colors = layerColors(layerList);
    let drawn = 0;
    let labels = 0;
    ctx.textAlign = 'left';
    for (const entity of model.entities.values()) {
        const layer = KIND_LAYER[entity.kind];
        if (!layers.has(layer)) {
            continue;
        }
        const [sx, sy] = camera.worldToScreen(entity.x, entity.y, width, height);
        if (sx < -8 || sy < -8 || sx > width + 8 || sy > height + 8) {
            continue;
        }
        ctx.fillStyle = entity.kind === 'creature' && entity.motion ? MOTION_COLORS[entity.motion] : (colors[layer] ?? '#C9D1D6');
        ctx.beginPath();
        if (entity.kind === 'character') {
            ctx.moveTo(sx, sy - 5);
            ctx.lineTo(sx + 5, sy);
            ctx.lineTo(sx, sy + 5);
            ctx.lineTo(sx - 5, sy);
            ctx.closePath();
        } else if (entity.kind === 'gameobject') {
            ctx.rect(sx - 3, sy - 3, 6, 6);
        } else {
            ctx.arc(sx, sy, 3.5, 0, Math.PI * 2);
        }
        ctx.fill();
        if (entity.id === selectedId) {
            ctx.strokeStyle = '#FFFFFF';
            ctx.lineWidth = 2;
            ctx.beginPath();
            ctx.arc(sx, sy, 9, 0, Math.PI * 2);
            ctx.stroke();
        }
        if (entity.name && camera.scale >= 0.35 && labels < 60) {
            ctx.fillStyle = '#E6ECEF';
            ctx.fillText(entity.name, sx + 8, sy);
            labels++;
        }
        drawn++;
    }
    return drawn;
}

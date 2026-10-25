// WebGL2 renderer.
//
// Per chunk (one layer, one tile part) the renderer keeps three GPU buffers expanded from the ATG1 arrays, each with
// its own vertex array, plus a palette lookup texture:
//   triangles  non-indexed, 16 bytes per vertex (x, y, z relative to the chunk origin, palette index)
//   lines      one instance per segment, 28 bytes (a.xyz, b.xyz, palette index), drawn as screen-space quads
//   points     one instance per point, 16 bytes (xyz, palette index)
// Everything is camera-relative: each draw sets uRel = chunk origin - eye, computed in double on the CPU, and the view
// matrix is rotation only.
//
// Frame passes (per region; a split view draws the server layers in the left scissor and the reference layers in the right):
//   1. depth pre-pass of every filled layer's triangles with polygon offset (hidden-line removal)
//   2. coloured fills (opaque first, then translucent without depth writes)
//   3. lines: wire/both layers depth-tested against the pre-pass, x-ray layers not
//   4. points
//   5. extra line and marker sets (probe overlays, live entities, selection): drawn depth-tested, then again where they
//      are occluded at reduced alpha, so they read "over everything" without hiding what is in front of them.
// A separate pick pass renders triangle ids into an RGBA32UI target under a one-pixel scissor.

import { buildPalette, styleRgbF } from './colors.js';
import { eye as cameraEye, projectionMatrix, viewMatrix } from './camera.js';
import { LINE_STRIDE, LINE_WORDS, MARKER_STRIDE } from './geom.js';
import * as S from './shaders.js';
import { writesDepth } from './layerstate.js';
import { createMaterialRegistry } from './materials/index.js';

export class RendererUnavailable extends Error {
    constructor(message) {
        super(message);
        this.name = 'RendererUnavailable';
    }
}

const TRI_STRIDE = 16;
const POINT_STRIDE = 16;
// Textured triangles: x, y, z relative to the chunk origin, u, v, colour0 as RGBA8.
const TEX_STRIDE = 24;
// Byte offsets of the streams in the expanded batch vertex (position, uv0, colour0), what a material's layout() names.
const TEX_STREAM_OFFSETS = { position: 0, uv0: 12, color0: 20 };
const LIGHT_WORLD = normalize3([0.35, 0.25, 0.9]);
const LINE_BIAS = [1e-4, 0.01];
const MARKER_BIAS = [1e-3, 0.6];
const GHOST_ALPHA = 0.32;

function normalize3(v) {
    const l = Math.hypot(v[0], v[1], v[2]);
    return [v[0] / l, v[1] / l, v[2] / l];
}

/** View-space direction of a world-space direction (the view matrix is rotation only). */
function rotateByView(view, v) {
    return [0, 1, 2].map((row) => view[row] * v[0] + view[4 + row] * v[1] + view[8 + row] * v[2]);
}

/**
 * Edges of triangles, each undirected edge once, for chunks that carry triangles but no lines (a source may omit the
 * wireframe; wire mode would otherwise show nothing for them).
 */
export function edgesFromTriangles(triangles, triangleAttributes, vertexCount) {
    const seen = new Set();
    const lines = [];
    const attrs = [];
    const edge = (a, b, attr) => {
        if (a === b) {
            return;
        }
        const key = a < b ? a * vertexCount + b : b * vertexCount + a;
        if (!seen.has(key)) {
            seen.add(key);
            lines.push(a, b);
            attrs.push(attr);
        }
    };
    for (let t = 0; t < triangleAttributes.length; t++) {
        const a = triangles[t * 3];
        const b = triangles[t * 3 + 1];
        const c = triangles[t * 3 + 2];
        edge(a, b, triangleAttributes[t]);
        edge(b, c, triangleAttributes[t]);
        edge(c, a, triangleAttributes[t]);
    }
    return { lines: Uint32Array.from(lines), lineAttributes: Uint32Array.from(attrs) };
}

export class Renderer {
    /** @param {HTMLCanvasElement} canvas */
    constructor(canvas, callbacks = {}) {
        this.canvas = canvas;
        this.callbacks = callbacks;
        const gl = canvas.getContext('webgl2', {
            antialias: true,
            alpha: false,
            depth: true,
            stencil: false,
            powerPreference: 'high-performance',
            preserveDrawingBuffer: false,
        });
        if (!gl) {
            throw new RendererUnavailable('WebGL2 is not available in this browser.');
        }
        this.gl = gl;
        // The texture counters are pinned names (design section 4): always numbers, 0 before any texture work.
        this.stats = {
            drawCalls: 0,
            triangles: 0,
            lines: 0,
            chunks: 0,
            gpuBytes: 0,
            textures: 0,
            textureBytes: 0,
            texturesPending: 0,
            texturesMissing: 0,
            texturesEvicted: 0,
            textureOverBudget: 0,
            arrayReallocs: 0,
        };
        this.materials = createMaterialRegistry();
        this.textures = null;
        this.shading = { mode: 0, contours: false, fog: true, sunAzimuth: 38, sunAltitude: 55 };
        this.pickSupported = true;
        this._slots = new Map();
        this._nextSlot = 1;
        this._pick = null;
        this._lost = false;
        canvas.addEventListener('webglcontextlost', (event) => {
            event.preventDefault();
            this._lost = true;
            this.callbacks.onContextLost?.();
        });
        canvas.addEventListener('webglcontextrestored', () => {
            this._lost = false;
            this._init();
            this.callbacks.onContextRestored?.();
        });
        this._init();
    }

    setShading(patchOrMode = {}, contoursArg = undefined) {
        const patch = typeof patchOrMode === 'number'
            ? { mode: patchOrMode, ...(typeof contoursArg === 'boolean' ? { contours: contoursArg } : {}) }
            : patchOrMode;
        if (!patch || typeof patch !== 'object') {
            return this.getShading();
        }
        if (patch.mode !== undefined) {
            const m = Number(patch.mode);
            if (Number.isInteger(m) && m >= 0 && m <= 3) {
                this.shading.mode = m;
            }
        }
        if (typeof patch.contours === 'boolean') {
            this.shading.contours = patch.contours;
        }
        if (typeof patch.fog === 'boolean') {
            this.shading.fog = patch.fog;
        }
        if (Number.isFinite(patch.sunAzimuth)) {
            this.shading.sunAzimuth = Number(patch.sunAzimuth);
        }
        if (Number.isFinite(patch.sunAltitude)) {
            this.shading.sunAltitude = Math.max(5, Math.min(89, Number(patch.sunAltitude)));
        }
        return this.getShading();
    }

    /**
     * Gives the renderer the store its material batches take textures from (a TextureStore, or null to draw textured chunks as flat
     * layer-coloured fills). The store belongs to the caller; a lost context needs a new one.
     */
    setTextureStore(store) {
        this.textures = store ?? null;
    }

    getShading() {
        return { ...this.shading, sunDir: this._sunDir() };
    }

    _sunDir() {
        const az = (this.shading.sunAzimuth * Math.PI) / 180;
        const alt = (this.shading.sunAltitude * Math.PI) / 180;
        const cosAlt = Math.cos(alt);
        return normalize3([-Math.cos(az) * cosAlt, -Math.sin(az) * cosAlt, Math.sin(alt)]);
    }

    get lost() {
        return this._lost;
    }

    static isSupported() {
        try {
            return document.createElement('canvas').getContext('webgl2') !== null;
        } catch {
            return false;
        }
    }

    _init() {
        const gl = this.gl;
        this._slots.clear();
        this._pick = null;
        this.stats.chunks = 0;
        this.stats.gpuBytes = 0;
        this.programs = {
            depth: this._program(S.DEPTH),
            fill: this._program(S.FILL),
            pick: this._program(S.PICK),
            line: this._program(S.LINE),
            marker: this._program(S.MARKER),
            material: null,
        };
        // The programs of the generic material come from its implementation, compiled through the same shader library.
        this.programs.material = this.materials.get('generic').programs(gl, { sources: S, compile: (source) => this._program(source) }).main;
        this.emptyLut = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, this.emptyLut);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, 256, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(256 * 4));
        this._textureParams();
        this.info = {
            renderer: this._debugRenderer(),
            maxTexture: gl.getParameter(gl.MAX_TEXTURE_SIZE),
            maxRenderbuffer: gl.getParameter(gl.MAX_RENDERBUFFER_SIZE),
            maxViewport: gl.getParameter(gl.MAX_VIEWPORT_DIMS),
            samples: gl.getParameter(gl.SAMPLES),
        };
    }

    _debugRenderer() {
        const gl = this.gl;
        const ext = gl.getExtension('WEBGL_debug_renderer_info');
        return ext ? String(gl.getParameter(ext.UNMASKED_RENDERER_WEBGL)) : String(gl.getParameter(gl.RENDERER));
    }

    _textureParams() {
        const gl = this.gl;
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    }

    _program({ vertex, fragment }) {
        const gl = this.gl;
        const compile = (type, source) => {
            const shader = gl.createShader(type);
            gl.shaderSource(shader, source);
            gl.compileShader(shader);
            if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
                const log = gl.getShaderInfoLog(shader);
                gl.deleteShader(shader);
                throw new RendererUnavailable(`Shader compile failed: ${log}`);
            }
            return shader;
        };
        const vs = compile(gl.VERTEX_SHADER, vertex);
        const fs = compile(gl.FRAGMENT_SHADER, fragment);
        const program = gl.createProgram();
        gl.attachShader(program, vs);
        gl.attachShader(program, fs);
        gl.linkProgram(program);
        gl.deleteShader(vs);
        gl.deleteShader(fs);
        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
            const log = gl.getProgramInfoLog(program);
            gl.deleteProgram(program);
            throw new RendererUnavailable(`Shader link failed: ${log}`);
        }
        const uniforms = {};
        const count = gl.getProgramParameter(program, gl.ACTIVE_UNIFORMS);
        for (let i = 0; i < count; i++) {
            const info = gl.getActiveUniform(program, i);
            uniforms[info.name] = gl.getUniformLocation(program, info.name);
        }
        return { program, u: uniforms };
    }

    /** Sets the drawing-buffer size in device pixels. */
    resize(width, height) {
        const w = Math.max(1, Math.floor(width));
        const h = Math.max(1, Math.floor(height));
        if (this.canvas.width !== w || this.canvas.height !== h) {
            this.canvas.width = w;
            this.canvas.height = h;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Chunks

    /**
     * Uploads a decoded ATG1 chunk. `info` is the layer's catalog entry (attributeColors for the palette). Returns the GPU
     * chunk; call destroyChunk when it leaves the window.
     */
    createChunk(decoded, info) {
        const gl = this.gl;
        const palette = buildPalette(
            [decoded.triangleAttributes, decoded.lineAttributes, decoded.pointAttributes],
            decoded.attributes,
            info?.attributeColors ?? {},
        );
        let { lines, lineAttributes } = decoded;
        let generatedLines = false;
        if (lines.length === 0 && decoded.triangleCount > 0) {
            ({ lines, lineAttributes } = edgesFromTriangles(decoded.triangles, decoded.triangleAttributes, decoded.vertexCount));
            generatedLines = true;
        }
        const triangleCount = decoded.triangleCount;
        const lineCount = lines.length / 2;
        const pointCount = decoded.pointCount;
        const p = decoded.positions;

        const chunk = {
            slot: this._nextSlot++,
            layerId: decoded.layer,
            tile: decoded.tile,
            part: decoded.part,
            origin: decoded.origin,
            bounds: null,
            triangleCount,
            lineCount,
            pointCount,
            vertexCount: decoded.vertexCount,
            attributes: decoded.attributes,
            attributeOf: palette.attributeOf,
            stats: decoded.stats,
            generatedLines,
            paletteRemapped: palette.remapped,
            bytes: 0,
            triVbo: null,
            lineVbo: null,
            pointVbo: null,
            triVao: null,
            lineVao: null,
            pointVao: null,
            texVbo: null,
            texVao: null,
            batches: [],
            materials: [],
            lut: null,
        };

        if (triangleCount > 0) {
            const buffer = new ArrayBuffer(triangleCount * 3 * TRI_STRIDE);
            const f = new Float32Array(buffer);
            const u = new Uint32Array(buffer);
            const tri = decoded.triangles;
            const attr = decoded.triangleAttributes;
            for (let t = 0; t < triangleCount; t++) {
                const pal = palette.paletteOf(attr[t]);
                for (let k = 0; k < 3; k++) {
                    const vi = tri[t * 3 + k] * 3;
                    const w = (t * 3 + k) * 4;
                    f[w] = p[vi];
                    f[w + 1] = p[vi + 1];
                    f[w + 2] = p[vi + 2];
                    u[w + 3] = pal;
                }
            }
            chunk.triVbo = this._buffer(buffer);
            chunk.triVao = this._vao(() => {
                gl.bindBuffer(gl.ARRAY_BUFFER, chunk.triVbo);
                gl.enableVertexAttribArray(0);
                gl.vertexAttribPointer(0, 3, gl.FLOAT, false, TRI_STRIDE, 0);
                gl.enableVertexAttribArray(1);
                gl.vertexAttribIPointer(1, 1, gl.UNSIGNED_INT, TRI_STRIDE, 12);
            });
            chunk.bytes += buffer.byteLength;
        }

        if (triangleCount > 0 && decoded.batches?.length > 0 && decoded.uv0) {
            // The batches draw from their own expanded copy (position, uv0, colour0); the plain triangle buffer above stays for the
            // depth pre-pass, picking and readTriangle, so a textured layer picks exactly like an untextured one.
            const buffer = new ArrayBuffer(triangleCount * 3 * TEX_STRIDE);
            const f = new Float32Array(buffer);
            const u = new Uint32Array(buffer);
            const tri = decoded.triangles;
            const uv = decoded.uv0;
            const color = decoded.color0;
            for (let t = 0; t < triangleCount * 3; t++) {
                const vertex = tri[t];
                const w = t * (TEX_STRIDE / 4);
                f[w] = p[vertex * 3];
                f[w + 1] = p[vertex * 3 + 1];
                f[w + 2] = p[vertex * 3 + 2];
                f[w + 3] = uv[vertex * 2];
                f[w + 4] = uv[vertex * 2 + 1];
                u[w + 5] = color ? color[vertex] : 0xFFFFFFFF;
            }
            chunk.texVbo = this._buffer(buffer);
            chunk.texVao = this._vao(() => {
                gl.bindBuffer(gl.ARRAY_BUFFER, chunk.texVbo);
                for (const a of this.materials.get('generic').layout()) {
                    gl.enableVertexAttribArray(a.location);
                    gl.vertexAttribPointer(a.location, a.size, gl[a.type], a.normalized, TEX_STRIDE, TEX_STREAM_OFFSETS[a.stream]);
                    if (a.divisor !== 0) {
                        gl.vertexAttribDivisor(a.location, a.divisor);
                    }
                }
            });
            chunk.bytes += buffer.byteLength;
            chunk.materials = decoded.materials;
            chunk.batches = decoded.batches.map((batch) => ({ ...batch, center: batchCenter(decoded, batch) }));
        }

        if (lineCount > 0) {
            const buffer = new ArrayBuffer(lineCount * LINE_STRIDE);
            const f = new Float32Array(buffer);
            const u = new Uint32Array(buffer);
            for (let i = 0; i < lineCount; i++) {
                const a = lines[i * 2] * 3;
                const b = lines[i * 2 + 1] * 3;
                const w = i * LINE_WORDS;
                f[w] = p[a];
                f[w + 1] = p[a + 1];
                f[w + 2] = p[a + 2];
                f[w + 3] = p[b];
                f[w + 4] = p[b + 1];
                f[w + 5] = p[b + 2];
                u[w + 6] = palette.paletteOf(lineAttributes[i]);
            }
            chunk.lineVbo = this._buffer(buffer);
            chunk.lineVao = this._lineVao(chunk.lineVbo);
            chunk.bytes += buffer.byteLength;
        }

        if (pointCount > 0) {
            const buffer = new ArrayBuffer(pointCount * POINT_STRIDE);
            const f = new Float32Array(buffer);
            const u = new Uint32Array(buffer);
            for (let i = 0; i < pointCount; i++) {
                const vi = decoded.points[i] * 3;
                const w = i * 4;
                f[w] = p[vi];
                f[w + 1] = p[vi + 1];
                f[w + 2] = p[vi + 2];
                u[w + 3] = palette.paletteOf(decoded.pointAttributes[i]);
            }
            chunk.pointVbo = this._buffer(buffer);
            chunk.pointVao = this._vao(() => {
                gl.bindBuffer(gl.ARRAY_BUFFER, chunk.pointVbo);
                gl.enableVertexAttribArray(0);
                gl.vertexAttribPointer(0, 3, gl.FLOAT, false, POINT_STRIDE, 0);
                gl.vertexAttribDivisor(0, 1);
                gl.enableVertexAttribArray(1);
                gl.vertexAttribIPointer(1, 1, gl.UNSIGNED_INT, POINT_STRIDE, 12);
                gl.vertexAttribDivisor(1, 1);
            });
            chunk.bytes += buffer.byteLength;
        }

        chunk.lut = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, chunk.lut);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, 256, palette.rows, 0, gl.RGBA, gl.UNSIGNED_BYTE, palette.rgba);
        this._textureParams();
        chunk.bytes += palette.rgba.byteLength;

        chunk.bounds = positionsBounds(p, decoded.origin);
        this._slots.set(chunk.slot, chunk);
        this.stats.chunks++;
        this.stats.gpuBytes += chunk.bytes;
        return chunk;
    }

    destroyChunk(chunk) {
        if (!chunk || !this._slots.delete(chunk.slot)) {
            return;
        }
        const gl = this.gl;
        for (const key of ['triVbo', 'lineVbo', 'pointVbo', 'texVbo']) {
            if (chunk[key]) {
                gl.deleteBuffer(chunk[key]);
            }
        }
        for (const key of ['triVao', 'lineVao', 'pointVao', 'texVao']) {
            if (chunk[key]) {
                gl.deleteVertexArray(chunk[key]);
            }
        }
        if (chunk.lut) {
            gl.deleteTexture(chunk.lut);
        }
        this.stats.chunks--;
        this.stats.gpuBytes -= chunk.bytes;
    }

    chunkBySlot(slot) {
        return this._slots.get(slot) ?? null;
    }

    _buffer(arrayBuffer) {
        const gl = this.gl;
        const buffer = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
        gl.bufferData(gl.ARRAY_BUFFER, arrayBuffer, gl.STATIC_DRAW);
        return buffer;
    }

    _vao(setup) {
        const gl = this.gl;
        const vao = gl.createVertexArray();
        gl.bindVertexArray(vao);
        setup();
        gl.bindVertexArray(null);
        return vao;
    }

    _lineVao(vbo) {
        const gl = this.gl;
        return this._vao(() => {
            gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
            gl.enableVertexAttribArray(0);
            gl.vertexAttribPointer(0, 3, gl.FLOAT, false, LINE_STRIDE, 0);
            gl.vertexAttribDivisor(0, 1);
            gl.enableVertexAttribArray(1);
            gl.vertexAttribPointer(1, 3, gl.FLOAT, false, LINE_STRIDE, 12);
            gl.vertexAttribDivisor(1, 1);
            gl.enableVertexAttribArray(2);
            gl.vertexAttribIPointer(2, 1, gl.UNSIGNED_INT, LINE_STRIDE, 24);
            gl.vertexAttribDivisor(2, 1);
        });
    }

    /** A dynamic set of thick lines: {origin, buffer, count} from LineBuilder.build(). */
    createLineSet(data) {
        const vbo = this._buffer(data.buffer);
        return { kind: 'lines', origin: data.origin, count: data.count, vbo, vao: this._lineVao(vbo) };
    }

    /** A dynamic set of markers: {origin, buffer, count} from MarkerBuilder.build(). */
    createMarkerSet(data) {
        const gl = this.gl;
        const vbo = this._buffer(data.buffer);
        const vao = this._vao(() => {
            gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
            gl.enableVertexAttribArray(0);
            gl.vertexAttribPointer(0, 3, gl.FLOAT, false, MARKER_STRIDE, 0);
            gl.vertexAttribDivisor(0, 1);
            gl.enableVertexAttribArray(1);
            gl.vertexAttribIPointer(1, 1, gl.UNSIGNED_INT, MARKER_STRIDE, 12);
            gl.vertexAttribDivisor(1, 1);
            gl.enableVertexAttribArray(2);
            gl.vertexAttribPointer(2, 1, gl.FLOAT, false, MARKER_STRIDE, 16);
            gl.vertexAttribDivisor(2, 1);
            gl.enableVertexAttribArray(3);
            gl.vertexAttribPointer(3, 1, gl.FLOAT, false, MARKER_STRIDE, 20);
            gl.vertexAttribDivisor(3, 1);
        });
        return { kind: 'markers', origin: data.origin, count: data.count, vbo, vao };
    }

    destroySet(set) {
        if (set) {
            this.gl.deleteBuffer(set.vbo);
            this.gl.deleteVertexArray(set.vao);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Frames

    /**
     * Draws a frame to the screen (target null) or the bound capture target. `frame`:
     *   camera, width, height (device pixels), pixelScale, background [r,g,b] 0..1, split (device-pixel x of the divider or null),
     *   layers: [{chunks, regions, color, opacity, mode, byAttribute, order}], sets: [{set, dashed?, width?, alpha, ghost?, scale?}]
     */
    render(frame) {
        const gl = this.gl;
        if (this._lost) {
            return;
        }
        this.stats.drawCalls = 0;
        this.stats.triangles = 0;
        this.stats.lines = 0;
        this.textures?.beginFrame();
        gl.viewport(0, 0, frame.width, frame.height);
        gl.disable(gl.SCISSOR_TEST);
        gl.depthMask(true);
        gl.colorMask(true, true, true, true);
        gl.clearColor(frame.background[0], frame.background[1], frame.background[2], 1);
        gl.clearDepth(1);
        gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
        const ctx = this._context(frame);
        if (frame.split === null || frame.split === undefined) {
            this._drawRegion(frame, ctx, 'full');
        } else {
            const x = Math.round(frame.split);
            gl.enable(gl.SCISSOR_TEST);
            gl.scissor(0, 0, x, frame.height);
            this._drawRegion(frame, ctx, 'left');
            gl.scissor(x, 0, frame.width - x, frame.height);
            this._drawRegion(frame, ctx, 'right');
            gl.disable(gl.SCISSOR_TEST);
        }
        gl.bindVertexArray(null);
        this._endTextureFrame();
    }

    _endTextureFrame() {
        const store = this.textures;
        if (!store) {
            return;
        }
        store.endFrame();
        const t = store.stats();
        this.stats.textures = t.resident;
        this.stats.textureBytes = t.bytes;
        this.stats.texturesPending = t.pending;
        this.stats.texturesMissing = t.missing;
        this.stats.texturesEvicted = t.evictions;
        this.stats.textureOverBudget = t.overBudget ? 1 : 0;
        this.stats.arrayReallocs = t.arrayReallocs;
    }

    /** True when the chunk's batches are drawn through the material pass (it has batches and a texture store exists). */
    _isTextured(chunk) {
        return chunk.texVao !== null && this.textures !== null;
    }

    _context(frame) {
        const camera = frame.camera;
        const view = new Float32Array(viewMatrix(camera));
        const proj = new Float32Array(projectionMatrix(camera, frame.aspect));
        return {
            eye: cameraEye(camera),
            view,
            proj,
            ortho: camera.mode === 'top',
            light: new Float32Array(rotateByView(view, LIGHT_WORLD)),
            sunDir: new Float32Array(this._sunDir()),
            viewport: new Float32Array([frame.width, frame.height]),
            scale: frame.pixelScale,
        };
    }

    _rel(ctx, origin) {
        return [origin[0] - ctx.eye[0], origin[1] - ctx.eye[1], origin[2] - ctx.eye[2]];
    }

    _drawRegion(frame, ctx, region) {
        const layers = frame.layers.filter((l) => l.chunks.length > 0 && l.regions.includes(region));
        this._passDepth(layers, ctx);
        this._passFill(layers, ctx);
        this._passMaterials(layers, ctx);
        this._passLines(layers, ctx);
        this._passPoints(layers, ctx);
        this._passSets(frame.sets ?? [], ctx);
    }

    _bindFrame(p, ctx, withViewport = false) {
        const gl = this.gl;
        gl.useProgram(p.program);
        gl.uniformMatrix4fv(p.u.uView, false, ctx.view);
        gl.uniformMatrix4fv(p.u.uProj, false, ctx.proj);
        if (withViewport) {
            gl.uniform2fv(p.u.uViewport, ctx.viewport);
            gl.uniform1i(p.u.uOrtho, ctx.ortho ? 1 : 0);
        }
    }

    _passDepth(layers, ctx) {
        const gl = this.gl;
        const p = this.programs.depth;
        let bound = false;
        for (const layer of layers) {
            if (!writesDepth(layer)) {
                continue;
            }
            if (!bound) {
                this._bindFrame(p, ctx);
                gl.enable(gl.DEPTH_TEST);
                gl.depthFunc(gl.LEQUAL);
                gl.depthMask(true);
                gl.disable(gl.BLEND);
                gl.colorMask(false, false, false, false);
                gl.enable(gl.POLYGON_OFFSET_FILL);
                gl.polygonOffset(1, 1);
                bound = true;
            }
            for (const chunk of layer.chunks) {
                // Textured chunks write their own depth in the material pass.
                if (chunk.triangleCount > 0 && !this._isTextured(chunk)) {
                    gl.uniform3fv(p.u.uRel, this._rel(ctx, chunk.origin));
                    gl.bindVertexArray(chunk.triVao);
                    gl.drawArrays(gl.TRIANGLES, 0, chunk.triangleCount * 3);
                    this._count(chunk.triangleCount, 0);
                }
            }
        }
        if (bound) {
            gl.colorMask(true, true, true, true);
            gl.disable(gl.POLYGON_OFFSET_FILL);
        }
    }

    _passFill(layers, ctx) {
        const gl = this.gl;
        const fills = layers.filter((l) => (l.mode === 'fill' || l.mode === 'both') && l.opacity > 0 && l.chunks.some((c) => c.triangleCount > 0));
        if (fills.length === 0) {
            return;
        }
        const p = this.programs.fill;
        this._bindFrame(p, ctx);
        gl.uniform3fv(p.u.uLight, ctx.light);
        gl.uniform1i(p.u.uLut, 0);
        gl.uniform1i(p.u.uShadeMode, this.shading.mode | 0);
        gl.uniform1i(p.u.uContours, this.shading.contours ? 1 : 0);
        gl.uniform1i(p.u.uFog, this.shading.fog ? 1 : 0);
        gl.uniform1i(p.u.uMode, ctx.ortho ? 0 : 1);
        gl.uniform3fv(p.u.uSunDir, ctx.sunDir);
        gl.enable(gl.DEPTH_TEST);
        gl.depthFunc(gl.LEQUAL);
        gl.activeTexture(gl.TEXTURE0);
        const draw = (layer, blended) => {
            const rgb = styleRgbF(layer.color);
            gl.uniform3f(p.u.uColor, rgb[0], rgb[1], rgb[2]);
            gl.uniform1i(p.u.uByAttribute, layer.byAttribute ? 1 : 0);
            gl.uniform1f(p.u.uAlpha, blended ? layer.opacity : 1);
            const isLiquid = layer.sub === 'liquid' || Boolean(layer.id?.includes('liquid')) || Boolean(layer.layerId?.includes('liquid'));
            gl.uniform1i(p.u.uIsLiquid, isLiquid ? 1 : 0);
            for (const chunk of layer.chunks) {
                if (chunk.triangleCount > 0 && !this._isTextured(chunk)) {
                    gl.bindTexture(gl.TEXTURE_2D, chunk.lut);
                    gl.uniform3fv(p.u.uOrigin, chunk.origin);
                    gl.uniform3fv(p.u.uRel, this._rel(ctx, chunk.origin));
                    gl.bindVertexArray(chunk.triVao);
                    gl.drawArrays(gl.TRIANGLES, 0, chunk.triangleCount * 3);
                    this._count(chunk.triangleCount, 0);
                }
            }
        };
        gl.disable(gl.BLEND);
        gl.depthMask(true);
        for (const layer of fills.filter((l) => l.opacity >= 0.99)) {
            draw(layer, false);
        }
        const translucent = fills.filter((l) => l.opacity < 0.99);
        if (translucent.length > 0) {
            gl.enable(gl.BLEND);
            gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
            gl.depthMask(false);
            for (const layer of translucent) {
                draw(layer, true);
            }
            gl.depthMask(true);
            gl.disable(gl.BLEND);
        }
    }

    /**
     * Draws the batches of textured chunks (ATG1 version 2): opaque first, then alpha key, then everything that blends, back to front
     * by the batch centre. A layer below full opacity draws all its batches in the blended group. Each batch asks the texture store
     * for its material's primary texture; the answer (ready, missing or still loading) decides what the shader samples.
     */
    _passMaterials(layers, ctx) {
        const store = this.textures;
        if (!store) {
            return;
        }
        const groups = { opaque: [], alphaKey: [], blended: [] };
        for (const layer of layers) {
            if ((layer.mode !== 'fill' && layer.mode !== 'both') || layer.opacity <= 0) {
                continue;
            }
            const faint = layer.opacity < 0.99;
            for (const chunk of layer.chunks) {
                if (!this._isTextured(chunk)) {
                    continue;
                }
                for (const batch of chunk.batches) {
                    const material = chunk.materials[batch.material];
                    const pass = this.materials.resolve(material.kind).pass(material);
                    const group = faint || pass === 'blend' ? groups.blended : pass === 'alphakey' ? groups.alphaKey : groups.opaque;
                    group.push({ layer, chunk, batch, material, faint });
                }
            }
        }
        if (groups.opaque.length + groups.alphaKey.length + groups.blended.length === 0) {
            return;
        }
        const eye = ctx.eye;
        const distance = (d) => (d.batch.center[0] - eye[0]) ** 2 + (d.batch.center[1] - eye[1]) ** 2 + (d.batch.center[2] - eye[2]) ** 2;
        groups.blended.sort((a, b) => distance(b) - distance(a));

        const gl = this.gl;
        const p = this.programs.material;
        this._bindFrame(p, ctx);
        gl.uniform3fv(p.u.uLight, ctx.light);
        gl.uniform3fv(p.u.uSunDir, ctx.sunDir);
        gl.uniform1i(p.u.uTexture, 0);
        gl.uniform1i(p.u.uMode, ctx.ortho ? 0 : 1);
        gl.depthFunc(gl.LEQUAL);
        gl.activeTexture(gl.TEXTURE0);
        for (const draw of [...groups.opaque, ...groups.alphaKey, ...groups.blended]) {
            this._drawBatch(p, ctx, store, draw);
        }
        gl.depthMask(true);
        gl.enable(gl.DEPTH_TEST);
        gl.disable(gl.BLEND);
    }

    _drawBatch(p, ctx, store, { layer, chunk, batch, material, faint }) {
        const gl = this.gl;
        const impl = this.materials.resolve(material.kind);
        const frame = { flat: styleRgbF(layer.color), alpha: layer.opacity, fog: this.shading.fog, faint };
        if (!impl.bind(gl, p, material, frame, store)) {
            return;
        }
        gl.uniform3fv(p.u.uRel, this._rel(ctx, chunk.origin));
        gl.bindVertexArray(chunk.texVao);
        gl.drawArrays(gl.TRIANGLES, batch.firstTriangle * 3, batch.triangleCount * 3);
        this._count(batch.triangleCount, 0);
    }

    _lineUniforms(p, ctx, { width, dash, bias, colorMode, rgb, alpha }) {
        const gl = this.gl;
        gl.uniform1f(p.u.uWidth, width * ctx.scale);
        gl.uniform1f(p.u.uDash, dash * ctx.scale);
        gl.uniform2f(p.u.uBias, bias[0], bias[1]);
        gl.uniform1i(p.u.uColorMode, colorMode);
        gl.uniform4f(p.u.uStyle, rgb[0], rgb[1], rgb[2], alpha);
    }

    _passLines(layers, ctx) {
        const gl = this.gl;
        const wired = layers.filter((l) => l.mode !== 'fill' && l.opacity > 0 && l.chunks.some((c) => c.lineCount > 0));
        if (wired.length === 0) {
            return;
        }
        const p = this.programs.line;
        this._bindFrame(p, ctx, true);
        gl.uniform1i(p.u.uLut, 0);
        gl.activeTexture(gl.TEXTURE0);
        gl.enable(gl.BLEND);
        gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
        gl.depthMask(false);
        gl.depthFunc(gl.LEQUAL);
        for (const layer of wired) {
            if (layer.mode === 'xray') {
                gl.disable(gl.DEPTH_TEST);
            } else {
                gl.enable(gl.DEPTH_TEST);
            }
            this._lineUniforms(p, ctx, {
                width: layer.lineWidth ?? 1.25,
                dash: 0,
                bias: LINE_BIAS,
                colorMode: layer.byAttribute ? 1 : 0,
                rgb: styleRgbF(layer.color),
                alpha: layer.opacity,
            });
            for (const chunk of layer.chunks) {
                if (chunk.lineCount > 0) {
                    gl.bindTexture(gl.TEXTURE_2D, chunk.lut);
                    gl.uniform3fv(p.u.uRel, this._rel(ctx, chunk.origin));
                    gl.bindVertexArray(chunk.lineVao);
                    gl.drawArraysInstanced(gl.TRIANGLES, 0, 6, chunk.lineCount);
                    this._count(0, chunk.lineCount);
                }
            }
        }
        gl.depthMask(true);
        gl.enable(gl.DEPTH_TEST);
        gl.disable(gl.BLEND);
    }

    _passPoints(layers, ctx) {
        const gl = this.gl;
        const pointed = layers.filter((l) => l.opacity > 0 && l.chunks.some((c) => c.pointCount > 0));
        if (pointed.length === 0) {
            return;
        }
        const p = this.programs.marker;
        this._bindFrame(p, ctx, true);
        gl.uniform1i(p.u.uLut, 0);
        gl.uniform1f(p.u.uScale, ctx.scale);
        gl.uniform2f(p.u.uBias, MARKER_BIAS[0], MARKER_BIAS[1]);
        gl.activeTexture(gl.TEXTURE0);
        gl.enable(gl.BLEND);
        gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
        gl.depthMask(false);
        gl.depthFunc(gl.LEQUAL);
        gl.vertexAttrib1f(2, 7);
        gl.vertexAttrib1f(3, 0);
        for (const layer of pointed) {
            if (layer.mode === 'xray') {
                gl.disable(gl.DEPTH_TEST);
            } else {
                gl.enable(gl.DEPTH_TEST);
            }
            const rgb = styleRgbF(layer.color);
            gl.uniform4f(p.u.uStyle, rgb[0], rgb[1], rgb[2], layer.opacity);
            gl.uniform1i(p.u.uColorMode, layer.byAttribute ? 1 : 0);
            for (const chunk of layer.chunks) {
                if (chunk.pointCount > 0) {
                    gl.bindTexture(gl.TEXTURE_2D, chunk.lut);
                    gl.uniform3fv(p.u.uRel, this._rel(ctx, chunk.origin));
                    gl.bindVertexArray(chunk.pointVao);
                    gl.drawArraysInstanced(gl.TRIANGLES, 0, 6, chunk.pointCount);
                    this._count(0, 0);
                }
            }
        }
        gl.depthMask(true);
        gl.enable(gl.DEPTH_TEST);
        gl.disable(gl.BLEND);
    }

    _passSets(sets, ctx) {
        const gl = this.gl;
        if (sets.length === 0) {
            return;
        }
        gl.enable(gl.BLEND);
        gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
        gl.depthMask(false);
        gl.enable(gl.DEPTH_TEST);
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, this.emptyLut);
        const passes = [
            { func: gl.LEQUAL, alpha: 1 },
            { func: gl.GREATER, alpha: GHOST_ALPHA },
        ];
        for (const { func, alpha } of passes) {
            gl.depthFunc(func);
            for (const entry of sets) {
                if (!entry.set || entry.set.count === 0 || (func === gl.GREATER && entry.ghost === false)) {
                    continue;
                }
                const a = (entry.alpha ?? 1) * alpha;
                if (entry.set.kind === 'lines') {
                    const p = this.programs.line;
                    this._bindFrame(p, ctx, true);
                    gl.uniform1i(p.u.uLut, 0);
                    this._lineUniforms(p, ctx, {
                        width: entry.width ?? 3,
                        dash: entry.dashed ? 12 : 0,
                        bias: LINE_BIAS,
                        colorMode: 2,
                        rgb: [1, 1, 1],
                        alpha: a,
                    });
                    gl.uniform3fv(p.u.uRel, this._rel(ctx, entry.set.origin));
                    gl.bindVertexArray(entry.set.vao);
                    gl.drawArraysInstanced(gl.TRIANGLES, 0, 6, entry.set.count);
                } else {
                    const p = this.programs.marker;
                    this._bindFrame(p, ctx, true);
                    gl.uniform1i(p.u.uLut, 0);
                    gl.uniform1f(p.u.uScale, ctx.scale * (entry.scale ?? 1));
                    gl.uniform2f(p.u.uBias, MARKER_BIAS[0], MARKER_BIAS[1]);
                    gl.uniform4f(p.u.uStyle, 1, 1, 1, a);
                    gl.uniform1i(p.u.uColorMode, 2);
                    gl.uniform3fv(p.u.uRel, this._rel(ctx, entry.set.origin));
                    gl.bindVertexArray(entry.set.vao);
                    gl.drawArraysInstanced(gl.TRIANGLES, 0, 6, entry.set.count);
                }
                this.stats.drawCalls++;
            }
        }
        gl.depthFunc(gl.LEQUAL);
        gl.depthMask(true);
        gl.disable(gl.BLEND);
    }

    _count(triangles, lines) {
        this.stats.drawCalls++;
        this.stats.triangles += triangles;
        this.stats.lines += lines;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Picking

    _ensurePick(width, height) {
        const gl = this.gl;
        if (this._pick && this._pick.width === width && this._pick.height === height) {
            return this._pick;
        }
        this._disposePick();
        const fbo = gl.createFramebuffer();
        const color = gl.createRenderbuffer();
        const depth = gl.createRenderbuffer();
        gl.bindRenderbuffer(gl.RENDERBUFFER, color);
        gl.renderbufferStorage(gl.RENDERBUFFER, gl.RGBA32UI, width, height);
        gl.bindRenderbuffer(gl.RENDERBUFFER, depth);
        gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT24, width, height);
        gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
        gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.RENDERBUFFER, color);
        gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, depth);
        const status = gl.checkFramebufferStatus(gl.FRAMEBUFFER);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        this._pick = { fbo, color, depth, width, height, ok: status === gl.FRAMEBUFFER_COMPLETE };
        if (!this._pick.ok) {
            this.pickSupported = false;
        }
        return this._pick;
    }

    _disposePick() {
        if (this._pick) {
            const gl = this.gl;
            gl.deleteFramebuffer(this._pick.fbo);
            gl.deleteRenderbuffer(this._pick.color);
            gl.deleteRenderbuffer(this._pick.depth);
            this._pick = null;
        }
    }

    /**
     * Finds the triangle under a device pixel (origin top-left): {chunk, triangle} or null. Uses the same layers and
     * regions as the screen, so what is hit is what is visible.
     */
    pick(frame, px, py) {
        const gl = this.gl;
        if (this._lost || !this.pickSupported) {
            return null;
        }
        const x = Math.floor(px);
        const y = Math.floor(py);
        if (x < 0 || y < 0 || x >= frame.width || y >= frame.height) {
            return null;
        }
        const target = this._ensurePick(frame.width, frame.height);
        if (!target.ok) {
            return null;
        }
        const region = frame.split === null || frame.split === undefined ? 'full' : x < frame.split ? 'left' : 'right';
        const layers = frame.layers.filter((l) => l.regions.includes(region) && l.chunks.some((c) => c.triangleCount > 0)
            && l.mode !== 'xray' && l.opacity > 0);
        gl.bindFramebuffer(gl.FRAMEBUFFER, target.fbo);
        gl.viewport(0, 0, frame.width, frame.height);
        gl.disable(gl.SCISSOR_TEST);
        gl.colorMask(true, true, true, true);
        gl.depthMask(true);
        gl.clearBufferuiv(gl.COLOR, 0, new Uint32Array([0, 0, 0, 0]));
        gl.clearBufferfv(gl.DEPTH, 0, new Float32Array([1]));
        gl.enable(gl.SCISSOR_TEST);
        gl.scissor(x, frame.height - 1 - y, 1, 1);
        gl.enable(gl.DEPTH_TEST);
        gl.depthFunc(gl.LESS);
        gl.disable(gl.BLEND);
        const ctx = this._context(frame);
        const p = this.programs.pick;
        this._bindFrame(p, ctx);
        for (const layer of layers) {
            for (const chunk of layer.chunks) {
                if (chunk.triangleCount > 0) {
                    gl.uniform3fv(p.u.uRel, this._rel(ctx, chunk.origin));
                    gl.uniform1ui(p.u.uSlot, chunk.slot);
                    gl.bindVertexArray(chunk.triVao);
                    gl.drawArrays(gl.TRIANGLES, 0, chunk.triangleCount * 3);
                }
            }
        }
        const out = new Uint32Array(4);
        gl.readPixels(x, frame.height - 1 - y, 1, 1, gl.RGBA_INTEGER, gl.UNSIGNED_INT, out);
        gl.disable(gl.SCISSOR_TEST);
        gl.bindVertexArray(null);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        gl.viewport(0, 0, this.canvas.width, this.canvas.height);
        if (out[0] === 0) {
            return null;
        }
        const chunk = this._slots.get(out[0]);
        return chunk ? { chunk, triangle: out[1] } : null;
    }

    /**
     * Reads one triangle back from the GPU: {points: [[x,y,z] * 3] in world space, attribute}. The palette index in the
     * vertex is mapped back to the chunk's attribute id.
     */
    readTriangle(chunk, triangle) {
        const gl = this.gl;
        if (!chunk.triVbo || triangle < 0 || triangle >= chunk.triangleCount) {
            return null;
        }
        const bytes = new Uint8Array(3 * TRI_STRIDE);
        gl.bindBuffer(gl.ARRAY_BUFFER, chunk.triVbo);
        gl.getBufferSubData(gl.ARRAY_BUFFER, triangle * 3 * TRI_STRIDE, bytes);
        const f = new Float32Array(bytes.buffer);
        const u = new Uint32Array(bytes.buffer);
        const points = [0, 1, 2].map((k) => [chunk.origin[0] + f[k * 4], chunk.origin[1] + f[k * 4 + 1], chunk.origin[2] + f[k * 4 + 2]]);
        return { points, attribute: chunk.attributeOf(u[3]) };
    }

    // ------------------------------------------------------------------------------------------------------------
    // Capture

    /** Largest image the capture path can produce on this GPU. */
    maxCaptureSize() {
        return Math.min(this.info.maxRenderbuffer, this.info.maxViewport[0], this.info.maxViewport[1]);
    }

    /**
     * Renders `frame` (whose width/height are the image size) into an off-screen multisampled target and returns
     * {width, height, data} as top-down RGBA bytes.
     */
    renderToPixels(frame) {
        const gl = this.gl;
        const { width, height } = frame;
        if (width > this.maxCaptureSize() || height > this.maxCaptureSize()) {
            throw new RangeError(`This GPU renders at most ${this.maxCaptureSize()} pixels per side; ${width}x${height} was requested.`);
        }
        const supported = gl.getInternalformatParameter(gl.RENDERBUFFER, gl.RGBA8, gl.SAMPLES) ?? [];
        const samples = supported.length > 0 ? [...supported].sort((a, b) => b - a).find((s) => s <= 4) ?? 0 : 0;
        const make = (multisampled) => {
            const fbo = gl.createFramebuffer();
            const color = gl.createRenderbuffer();
            const depth = gl.createRenderbuffer();
            gl.bindRenderbuffer(gl.RENDERBUFFER, color);
            if (multisampled) {
                gl.renderbufferStorageMultisample(gl.RENDERBUFFER, samples, gl.RGBA8, width, height);
            } else {
                gl.renderbufferStorage(gl.RENDERBUFFER, gl.RGBA8, width, height);
            }
            gl.bindRenderbuffer(gl.RENDERBUFFER, depth);
            if (multisampled) {
                gl.renderbufferStorageMultisample(gl.RENDERBUFFER, samples, gl.DEPTH_COMPONENT24, width, height);
            } else {
                gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT24, width, height);
            }
            gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
            gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.RENDERBUFFER, color);
            gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, depth);
            return { fbo, color, depth };
        };
        const target = make(samples > 0);
        const resolve = samples > 0 ? make(false) : null;
        try {
            gl.bindFramebuffer(gl.FRAMEBUFFER, target.fbo);
            if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) {
                throw new Error('The off-screen capture target is not complete on this GPU.');
            }
            this.render(frame);
            let readFbo = target.fbo;
            if (resolve) {
                gl.bindFramebuffer(gl.READ_FRAMEBUFFER, target.fbo);
                gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, resolve.fbo);
                gl.blitFramebuffer(0, 0, width, height, 0, 0, width, height, gl.COLOR_BUFFER_BIT, gl.NEAREST);
                readFbo = resolve.fbo;
            }
            gl.bindFramebuffer(gl.READ_FRAMEBUFFER, readFbo);
            const bottomUp = new Uint8Array(width * height * 4);
            gl.readPixels(0, 0, width, height, gl.RGBA, gl.UNSIGNED_BYTE, bottomUp);
            const data = new Uint8ClampedArray(width * height * 4);
            const row = width * 4;
            for (let y = 0; y < height; y++) {
                data.set(bottomUp.subarray((height - 1 - y) * row, (height - y) * row), y * row);
            }
            return { width, height, data };
        } finally {
            gl.bindFramebuffer(gl.FRAMEBUFFER, null);
            for (const t of [target, resolve]) {
                if (t) {
                    gl.deleteFramebuffer(t.fbo);
                    gl.deleteRenderbuffer(t.color);
                    gl.deleteRenderbuffer(t.depth);
                }
            }
            gl.viewport(0, 0, this.canvas.width, this.canvas.height);
        }
    }

    dispose() {
        for (const chunk of [...this._slots.values()]) {
            this.destroyChunk(chunk);
        }
        this._disposePick();
    }
}

/** World-space centre of the bounding box of one batch's triangles (the sort key of blended batches). */
function batchCenter(decoded, batch) {
    const p = decoded.positions;
    const tri = decoded.triangles;
    const min = [Infinity, Infinity, Infinity];
    const max = [-Infinity, -Infinity, -Infinity];
    for (let i = batch.firstTriangle * 3; i < (batch.firstTriangle + batch.triangleCount) * 3; i++) {
        for (let a = 0; a < 3; a++) {
            const v = p[tri[i] * 3 + a];
            if (v < min[a]) {
                min[a] = v;
            }
            if (v > max[a]) {
                max[a] = v;
            }
        }
    }
    return [0, 1, 2].map((a) => decoded.origin[a] + (min[a] + max[a]) / 2);
}

function positionsBounds(p, origin) {
    if (p.length === 0) {
        return null;
    }
    const min = [Infinity, Infinity, Infinity];
    const max = [-Infinity, -Infinity, -Infinity];
    for (let i = 0; i < p.length; i += 3) {
        for (let a = 0; a < 3; a++) {
            const v = p[i + a];
            if (v < min[a]) {
                min[a] = v;
            }
            if (v > max[a]) {
                max[a] = v;
            }
        }
    }
    return {
        min: [min[0] + origin[0], min[1] + origin[1], min[2] + origin[2]],
        max: [max[0] + origin[0], max[1] + origin[1], max[2] + origin[2]],
    };
}

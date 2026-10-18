// Minimal PNG reader for tests: 8-bit non-interlaced grayscale, RGB and RGBA, all five scanline filters.

import zlib from 'node:zlib';

const SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

export function isPng(buffer) {
    return buffer.length > 8 && buffer.subarray(0, 8).equals(SIGNATURE);
}

/** @returns {{width: number, height: number, rgba: Uint8Array}} */
export function decodePng(buffer) {
    if (!isPng(buffer)) {
        throw new Error('Not a PNG.');
    }
    let offset = 8;
    let header = null;
    const idat = [];
    while (offset < buffer.length) {
        const length = buffer.readUInt32BE(offset);
        const type = buffer.toString('latin1', offset + 4, offset + 8);
        const data = buffer.subarray(offset + 8, offset + 8 + length);
        if (type === 'IHDR') {
            header = { width: data.readUInt32BE(0), height: data.readUInt32BE(4), depth: data[8], color: data[9], interlace: data[12] };
        } else if (type === 'IDAT') {
            idat.push(data);
        } else if (type === 'IEND') {
            break;
        }
        offset += 12 + length;
    }
    if (!header || header.depth !== 8 || header.interlace !== 0 || ![0, 2, 6].includes(header.color)) {
        throw new Error(`Unsupported PNG (${JSON.stringify(header)}).`);
    }
    const channels = header.color === 6 ? 4 : header.color === 2 ? 3 : 1;
    const stride = header.width * channels;
    const raw = zlib.inflateSync(Buffer.concat(idat));
    if (raw.length !== (stride + 1) * header.height) {
        throw new Error(`PNG data is ${raw.length} bytes, expected ${(stride + 1) * header.height}.`);
    }
    const pixels = new Uint8Array(stride * header.height);
    for (let y = 0; y < header.height; y++) {
        const filter = raw[y * (stride + 1)];
        const line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
        const row = pixels.subarray(y * stride, (y + 1) * stride);
        const prev = y > 0 ? pixels.subarray((y - 1) * stride, y * stride) : new Uint8Array(stride);
        for (let x = 0; x < stride; x++) {
            const a = x >= channels ? row[x - channels] : 0;
            const b = prev[x];
            const c = x >= channels ? prev[x - channels] : 0;
            let v = line[x];
            if (filter === 1) {
                v += a;
            } else if (filter === 2) {
                v += b;
            } else if (filter === 3) {
                v += (a + b) >> 1;
            } else if (filter === 4) {
                const p = a + b - c;
                const pa = Math.abs(p - a);
                const pb = Math.abs(p - b);
                const pc = Math.abs(p - c);
                v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
            } else if (filter !== 0) {
                throw new Error(`Bad PNG filter ${filter}.`);
            }
            row[x] = v & 255;
        }
    }
    const rgba = new Uint8Array(header.width * header.height * 4);
    for (let i = 0; i < header.width * header.height; i++) {
        if (channels === 4) {
            rgba.set(pixels.subarray(i * 4, i * 4 + 4), i * 4);
        } else if (channels === 3) {
            rgba.set(pixels.subarray(i * 3, i * 3 + 3), i * 4);
            rgba[i * 4 + 3] = 255;
        } else {
            rgba.fill(pixels[i], i * 4, i * 4 + 3);
            rgba[i * 4 + 3] = 255;
        }
    }
    return { width: header.width, height: header.height, rgba };
}

/** How many pixels differ from `background` ([r,g,b], per-channel tolerance), and the count within `box`. */
export function countNot(image, background, tolerance = 6, box = null) {
    const x0 = box?.x ?? 0;
    const y0 = box?.y ?? 0;
    const x1 = box ? box.x + box.w : image.width;
    const y1 = box ? box.y + box.h : image.height;
    let n = 0;
    for (let y = y0; y < y1; y++) {
        for (let x = x0; x < x1; x++) {
            const i = (y * image.width + x) * 4;
            if (Math.abs(image.rgba[i] - background[0]) > tolerance || Math.abs(image.rgba[i + 1] - background[1]) > tolerance || Math.abs(image.rgba[i + 2] - background[2]) > tolerance) {
                n++;
            }
        }
    }
    return n;
}

/** Counts pixels near `color` ([r,g,b]) within the tolerance. */
export function countNear(image, color, tolerance = 40, box = null) {
    const x0 = box?.x ?? 0;
    const y0 = box?.y ?? 0;
    const x1 = box ? box.x + box.w : image.width;
    const y1 = box ? box.y + box.h : image.height;
    let n = 0;
    for (let y = y0; y < y1; y++) {
        for (let x = x0; x < x1; x++) {
            const i = (y * image.width + x) * 4;
            if (Math.abs(image.rgba[i] - color[0]) <= tolerance && Math.abs(image.rgba[i + 1] - color[1]) <= tolerance && Math.abs(image.rgba[i + 2] - color[2]) <= tolerance) {
                n++;
            }
        }
    }
    return n;
}

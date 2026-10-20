// The top bar's coordinate box. Accepts
//   x, y, z          current map          map x y z        (also with commas)
//   x, y             current map, height kept
// Separators are commas, semicolons or spaces; brackets are ignored. Anything else is refused with a message that
// says what to type.

import { parseNumber } from './forms.js';

const LIMIT = 1_000_000;

export const LANDMARKS = [
    { name: 'Goldshire', map: 0, x: -9462.66, y: 16.19, z: 57.05 },
    { name: 'Stormwind Keep', map: 0, x: -8423.0, y: 334.0, z: 121.0 },
    { name: 'Ironforge Gates', map: 0, x: -5032.0, y: -819.0, z: 495.0 },
    { name: 'Darnassus Temple', map: 1, x: 9951.8, y: 2280.4, z: 1341.4 },
    { name: 'Blackrock Mountain', map: 0, x: -7535.0, y: -1212.0, z: 285.0 },
    { name: 'Orgrimmar Valley of Strength', map: 1, x: 1629.36, y: -4373.39, z: 31.26 },
    { name: 'Thunder Bluff', map: 1, x: -1277.37, y: 124.81, z: 131.29 },
    { name: 'Crossroads', map: 1, x: -443.1, y: -2598.1, z: 96.0 },
    { name: 'Ratchet', map: 1, x: -956.66, y: -3754.71, z: 5.33 },
    { name: 'Tarren Mill', map: 0, x: -31.4, y: -927.3, z: 54.3 },
    { name: 'Darkshire', map: 0, x: -10531.7, y: -1157.6, z: 27.9 },
    { name: 'Gadgetzan', map: 1, x: -7177.16, y: -3785.34, z: 8.37 },
    { name: 'Menethil Harbor', map: 0, x: -3727.4, y: -731.9, z: 8.9 },
    { name: "Light's Hope Chapel", map: 0, x: 2282.7, y: -5317.5, z: 88.6 },
];

export function findLandmark(query) {
    const q = String(query ?? '').trim().toLowerCase();
    if (q.length < 3 || /[,;]/.test(q)) {
        return null;
    }
    return LANDMARKS.find((l) => l.name.toLowerCase() === q)
        ?? LANDMARKS.find((l) => l.name.toLowerCase().includes(q))
        ?? null;
}

export function parseGoto(text, currentMap) {
    const raw = String(text ?? '').trim();
    if (raw !== '' && /[a-zA-Z]/.test(raw)) {
        const landmark = findLandmark(raw);
        if (landmark) {
            return { map: landmark.map, x: landmark.x, y: landmark.y, z: landmark.z };
        }
    }
    const tokens = String(text).replace(/[[\]()]/g, ' ').split(/[\s,;]+/).filter(Boolean);
    if (tokens.length < 2 || tokens.length > 4) {
        return { error: 'Type x, y, z or map x y z.' };
    }
    const numbers = tokens.map(parseNumber);
    if (numbers.some((n) => n === null)) {
        return { error: 'Coordinates must be plain numbers, for example -9462.66, 16.19, 57.05.' };
    }
    if (numbers.some((n) => Math.abs(n) > LIMIT)) {
        return { error: 'A coordinate is out of range.' };
    }
    let map = currentMap;
    let rest = numbers;
    if (numbers.length === 4) {
        const [m, ...coords] = numbers;
        if (!Number.isInteger(m) || m < 0) {
            return { error: 'The map id is a whole number, 0 or more.' };
        }
        map = m;
        rest = coords;
    }
    return { map, x: rest[0], y: rest[1], z: rest.length === 3 ? rest[2] : null };
}

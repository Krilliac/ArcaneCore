// The top bar's coordinate box. Accepts
//   x, y, z          current map          map x y z        (also with commas)
//   x, y             current map, height kept
// Separators are commas, semicolons or spaces; brackets are ignored. Anything else is refused with a message that
// says what to type.

import { parseNumber } from './forms.js';

const LIMIT = 1_000_000;

export function parseGoto(text, currentMap) {
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

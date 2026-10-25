// Reads the `Syntax:` help line of a GM command (the daemon's own help text, also printed in docs/reference/gm-commands.md) into an
// argument list the palette turns into a form: `#n` is a number, `$name` and `<name>` are text, `[...]` is optional, `a|b` is a choice,
// `key=default` carries a default and `--flag` is a switch. A line the grammar does not cover is reported as such and the palette falls back
// to a free-text field. Pure: no DOM.

const ENTITIES = { '&lt;': '<', '&gt;': '>', '&amp;': '&', '&quot;': '"', '&#39;': "'" };
const DASHES = new Set(['—', '–', '-', '--']);

/** Help text as written: markdown-escaped pipes and HTML entities undone. */
export function decodeHelp(text) {
    return String(text ?? '').replace(/\\\|/g, '|').replace(/&(?:lt|gt|amp|quot|#39);/g, (m) => ENTITIES[m]).replace(/\s+/g, ' ').trim();
}

/** Splits at spaces that are outside (), [] and <>; returns null when the brackets do not balance. */
function tokenize(text) {
    const tokens = [];
    let depth = 0;
    let current = '';
    const closers = { '[': ']', '(': ')', '<': '>' };
    const stack = [];
    for (const ch of text) {
        if (ch === '[' || ch === '(' || ch === '<') {
            stack.push(closers[ch]);
            depth++;
        } else if (stack.length > 0 && ch === stack[stack.length - 1]) {
            stack.pop();
            depth--;
        } else if ((ch === ']' || ch === ')') && depth === 0) {
            return null;
        }
        if (ch === ' ' && depth === 0) {
            if (current !== '') {
                tokens.push(current);
            }
            current = '';
        } else {
            current += ch;
        }
    }
    if (depth !== 0) {
        return null;
    }
    if (current !== '') {
        tokens.push(current);
    }
    return tokens;
}

const NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;

/** One token (without optional brackets) -> an argument, or null when it is not argument syntax. */
function parseAtom(token, optional) {
    if (token.startsWith('#') && NAME.test(token.slice(1))) {
        return { kind: 'number', name: token.slice(1), optional };
    }
    if (token.startsWith('$') && NAME.test(token.slice(1))) {
        return { kind: 'text', name: token.slice(1), optional };
    }
    if (token.startsWith('<') && token.endsWith('>')) {
        const inner = token.slice(1, -1).trim();
        return inner === '' ? null : { kind: 'text', name: inner, optional };
    }
    if (token.startsWith('--') && /^--[a-z][a-z0-9-]*$/.test(token)) {
        return { kind: 'flag', name: token, optional: true };
    }
    if (/^0x[A-Z_]*GUID$/i.test(token)) {
        return { kind: 'text', name: 'guid', optional, hint: '0x and hex digits' };
    }
    if (token.includes('|') || /^[a-z]+(?:\/[a-z]+)+$/.test(token)) {
        const options = token.replace(/^\(|\)$/g, '').split(/[|/]/).map((o) => o.trim()).filter((o) => o !== '');
        if (options.length < 2) {
            return null;
        }
        const free = options.some((o) => o.startsWith('#') || o.startsWith('$') || o.startsWith('<'));
        return { kind: 'choice', name: options.filter((o) => !/^[#$<]/.test(o)).join('|'), options: options.map((o) => o.replace(/^[#$]/, '')), free, optional };
    }
    const keyed = /^([a-z][A-Za-z0-9_]*)=([^\s=]+)$/.exec(token);
    if (keyed) {
        return { kind: /^-?\d+(\.\d+)?$/.test(keyed[2]) ? 'number' : 'text', name: keyed[1], optional: true, default: keyed[2], keyed: true };
    }
    if (/^[a-z][a-z0-9_]*$/.test(token)) {
        return { kind: 'literal', name: token, optional };
    }
    return null;
}

/** A `[...]` or `(...)` group -> arguments (all optional for `[`), recursively; null when something in it is not argument syntax. */
function parseGroup(token) {
    const optional = token.startsWith('[');
    const inner = token.slice(1, -1).trim();
    if (inner === '') {
        return null;
    }
    const tokens = tokenize(inner);
    if (tokens === null) {
        return null;
    }
    const out = [];
    for (const t of tokens) {
        const parsed = parseToken(t, optional);
        if (parsed === null) {
            return null;
        }
        out.push(...parsed);
    }
    return out;
}

function parseToken(token, optional) {
    if (token.startsWith('[') && token.endsWith(']')) {
        return parseGroup(token);
    }
    if (token.startsWith('(') && token.endsWith(')')) {
        const inner = token.slice(1, -1);
        if (inner.includes('|') && !inner.includes(' ')) {
            const atom = parseAtom(inner, optional);
            return atom ? [atom] : null;
        }
        // A parenthesised alternative with several words: keep the first form and let the rest be typed.
        const first = inner.split('|')[0].trim();
        const tokens = tokenize(first);
        if (tokens === null) {
            return null;
        }
        const out = [];
        for (const t of tokens) {
            const parsed = parseToken(t, optional);
            if (parsed === null) {
                return null;
            }
            out.push(...parsed);
        }
        return out.length > 0 ? out : null;
    }
    const atom = parseAtom(token, optional);
    return atom ? [atom] : null;
}

/** True when a token starts the prose that follows the syntax: a dash, a capitalised word, a quote, a digit or a command path. */
const startsDescription = (token) => DASHES.has(token) || /^[A-Z"'.]|^\d/.test(token) && !/^0x[A-Z_]*GUID$/i.test(token) || (token.startsWith('(') && token.includes(' ') && !token.includes('|'));

/**
 * Parses a help line.
 * @param {string} help  the command's help text; it must start with `Syntax:`.
 * @param {string} path  the command path, with or without the leading dot (`cheat god`).
 * @returns {{ok: boolean, partial: boolean, args: object[], reason?: string}}
 *   ok: the arguments were read; partial: an alternative form (`|` at the top level) was cut off, so the form also offers a free tail.
 */
export function parseSyntax(help, path) {
    const text = decodeHelp(help);
    const match = /^Syntax:\s*(.*)$/s.exec(text);
    if (!match) {
        return { ok: false, partial: false, args: [], reason: 'no Syntax: line' };
    }
    const tokens = tokenize(match[1]);
    if (tokens === null || tokens.length === 0) {
        return { ok: false, partial: false, args: [], reason: 'unbalanced brackets' };
    }
    const words = path.replace(/^\./, '').split(' ').filter((w) => w !== '');
    if (tokens[0].replace(/^\./, '') !== words[0]) {
        return { ok: false, partial: false, args: [], reason: 'the syntax line names another command' };
    }
    let index = 1;
    for (let w = 1; w < words.length; w++) {
        if (tokens[index] === words[w]) {
            index++;
        } else {
            break;
        }
    }
    const args = [];
    let partial = false;
    for (; index < tokens.length; index++) {
        const token = tokens[index];
        if (token === '|') {
            partial = true;
            break;
        }
        if (startsDescription(token)) {
            break;
        }
        const parsed = parseToken(token, false);
        if (parsed === null) {
            // Not argument syntax and not obviously prose: keep what was read and let the operator type the rest.
            partial = true;
            break;
        }
        args.push(...parsed);
    }
    return { ok: true, partial, args };
}

/**
 * The command line for a parsed syntax and the values a form collected: literals are emitted as written, an empty optional is skipped, a
 * value with a space or a line break is refused (null) because the daemon reads one command per line. `values` maps argument name to text.
 */
export function buildLine(path, args, values, tail = '') {
    const parts = [path.replace(/^\./, '')];
    for (const arg of args) {
        if (arg.kind === 'literal') {
            parts.push(arg.name);
            continue;
        }
        const raw = values[arg.name];
        if (arg.kind === 'flag') {
            if (raw === true || raw === 'true') {
                parts.push(arg.name);
            }
            continue;
        }
        const value = raw === undefined || raw === null || raw === '' ? (arg.optional ? '' : (arg.default ?? '')) : String(raw);
        if (value === '') {
            if (!arg.optional) {
                return null;
            }
            continue;
        }
        if (/[\r\n]/.test(value)) {
            return null;
        }
        parts.push(arg.keyed ? `${arg.name}=${value}` : value);
    }
    const extra = String(tail ?? '').trim();
    if (extra !== '') {
        if (/[\r\n]/.test(extra)) {
            return null;
        }
        parts.push(extra);
    }
    return parts.join(' ');
}

/**
 * The leaf rows (commands without sub-commands) of the generated GM command reference: `[{path, help, level, security}]`. A group row is
 * the one whose command cell ends in `...`.
 */
export function leafHelpLines(markdown) {
    const rows = [];
    for (const line of String(markdown).split(/\r?\n/)) {
        if (!line.startsWith('| `.')) {
            continue;
        }
        const cells = line.replace(/^\|/, '').split(/(?<!\\)\|/).map((c) => c.trim());
        if (cells.length < 5) {
            continue;
        }
        const path = /^`([^`]+)`/.exec(cells[0]);
        if (!path || /`\s*\.\.\.\s*$/.test(cells[0])) {
            continue;
        }
        const tail = cells.slice(4);
        if (tail[tail.length - 1] === '') {
            tail.pop();
        }
        rows.push({ path: path[1], level: Number(cells[1]), security: cells[2], help: tail.join('|') });
    }
    return rows;
}

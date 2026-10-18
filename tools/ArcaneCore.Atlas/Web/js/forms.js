// Argument forms generated from a command's JSON schema (CommandSchema.InputSchema in Core/Commands/CommandSchema.cs:
// an object schema whose properties are string, integer, number, boolean, a 3-number array, a string array, or an
// object, optionally with `enum`, `default` and `description`, plus a `required` list). Pure logic; the DOM lives in
// ui/formview.js.

import { formatNumber } from './util.js';

/** Field descriptors in schema order: {name, kind, required, description, default, choices}. */
export function fieldsFromSchema(schema) {
    const properties = schema?.properties ?? {};
    const required = new Set(Array.isArray(schema?.required) ? schema.required : []);
    return Object.entries(properties).map(([name, p]) => ({
        name,
        kind: kindOf(p),
        required: required.has(name),
        description: typeof p?.description === 'string' ? p.description : '',
        default: p?.default,
        choices: Array.isArray(p?.enum) ? p.enum.map(String) : null,
    }));
}

function kindOf(p) {
    if (Array.isArray(p?.enum) && p.enum.length > 0) {
        return 'choice';
    }
    switch (p?.type) {
        case 'string':
            return 'string';
        case 'integer':
            return 'integer';
        case 'number':
            return 'number';
        case 'boolean':
            return 'boolean';
        case 'array':
            if (p.items?.type === 'number' && p.minItems === 3 && p.maxItems === 3) {
                return 'point3';
            }
            return p.items?.type === 'string' ? 'stringList' : 'json';
        default:
            return 'json';
    }
}

/** The text a field starts with: its default, formatted for typing. */
export function defaultText(field) {
    const d = field.default;
    if (d === undefined || d === null) {
        return '';
    }
    switch (field.kind) {
        case 'point3':
            return Array.isArray(d) ? d.join(', ') : String(d);
        case 'stringList':
            return Array.isArray(d) ? d.join(', ') : String(d);
        case 'json':
            return typeof d === 'string' ? d : JSON.stringify(d);
        default:
            return String(d);
    }
}

const NUMBER = /^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$/;

/** Parses a number token strictly (no hex, no "Infinity", no empty string). */
export function parseNumber(text) {
    const t = String(text).trim();
    if (!NUMBER.test(t)) {
        return null;
    }
    const v = Number(t);
    return Number.isFinite(v) ? v : null;
}

/** "x, y, z" or "x y z" (also "[x, y, z]" and "(x, y, z)") -> [x, y, z], or null. */
export function parsePoint(text) {
    const parts = String(text).replace(/[[\]()]/g, ' ').split(/[\s,;]+/).filter(Boolean);
    if (parts.length !== 3) {
        return null;
    }
    const values = parts.map(parseNumber);
    return values.every((v) => v !== null) ? values : null;
}

export function formatPointText(p, digits = 2) {
    return Array.isArray(p) ? p.map((v) => formatNumber(v, digits)).join(', ') : '';
}

/**
 * Converts one field's text to a JSON value. Returns {skip: true} for an empty optional field, {value} on success and
 * {error} with a message that says what to type.
 */
export function parseField(field, text) {
    const raw = typeof text === 'string' ? text.trim() : text;
    if (raw === '' || raw === null || raw === undefined) {
        return field.required ? { error: `${field.name} is required.` } : { skip: true };
    }
    switch (field.kind) {
        case 'string':
            return { value: String(text) };
        case 'choice':
            return field.choices.includes(raw) ? { value: raw } : { error: `${field.name} must be one of ${field.choices.join(', ')}.` };
        case 'boolean':
            return raw === 'true' || raw === true ? { value: true } : raw === 'false' || raw === false ? { value: false } : { error: `${field.name} must be true or false.` };
        case 'integer': {
            const v = parseNumber(raw);
            return v !== null && Number.isInteger(v) ? { value: v } : { error: `${field.name} must be a whole number.` };
        }
        case 'number': {
            const v = parseNumber(raw);
            return v !== null ? { value: v } : { error: `${field.name} must be a number.` };
        }
        case 'point3': {
            const v = parsePoint(raw);
            return v ? { value: v } : { error: `${field.name} must be three numbers: x, y, z.` };
        }
        case 'stringList':
            return { value: String(raw).split(',').map((s) => s.trim()).filter(Boolean) };
        default:
            try {
                const parsed = JSON.parse(raw);
                return parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)
                    ? { value: parsed }
                    : { error: `${field.name} must be a JSON object, for example {"key": 1}.` };
            } catch {
                return { error: `${field.name} must be valid JSON.` };
            }
    }
}

/**
 * Builds the args object from field texts ({name: text}). Returns {args, errors}; errors is {name: message} and args is
 * only meaningful when it is empty.
 */
export function buildArgs(fields, texts) {
    const args = {};
    const errors = {};
    for (const field of fields) {
        const r = parseField(field, texts[field.name] ?? '');
        if (r.error) {
            errors[field.name] = r.error;
        } else if (!r.skip) {
            args[field.name] = r.value;
        }
    }
    return { args, errors };
}

/** True when a command declares an argument called `name` (so a probe can drop what the paired command does not take). */
export function acceptsArg(schema, name) {
    return Object.prototype.hasOwnProperty.call(schema?.properties ?? {}, name);
}

/** Keeps only the args a schema declares. */
export function filterArgs(schema, args) {
    const out = {};
    for (const [k, v] of Object.entries(args)) {
        if (acceptsArg(schema, k)) {
            out[k] = v;
        }
    }
    return out;
}

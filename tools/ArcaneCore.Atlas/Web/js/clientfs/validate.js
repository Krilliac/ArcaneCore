// Validation of a picked folder (design 0.5): runs in a Worker before anything is cached or stored. It decides from data, not
// from names alone: the archive set, the MPQ headers, the DBC layouts of build 5875 (CreatureDisplayInfo 12 fields, Map 42,
// AreaTable 25) and, when WoW.exe is there, its VS_VERSION_INFO resource. The folder object comes from scope.js.
//
// Error texts are part of the UI contract (dialog.js shows errors[0]).

import { parseDbc } from './dbc.js';
import { ClientFsError } from './limits.js';
import { createChain, loadOrder, openArchive } from './mpq.js';

export const EXPECTED_BUILD = 5875;
export const EXPECTED_VERSION = '1.12.1.5875';
export const ERROR_NO_DATA = 'No Data folder here - pick the folder that contains WoW.exe and Data';
export const ERROR_BC = 'This is a Burning Crusade or later client; Atlas needs 1.12.1';

const REQUIRED = ['dbc', 'model', 'texture', 'terrain', 'wmo', 'patch'];
const LATER_CLIENTS = new Set(['common.mpq', 'expansion.mpq', 'lichking.mpq']);
const LAYOUTS = [
    { path: 'DBFilesClient\\CreatureDisplayInfo.dbc', fields: 12, name: 'CreatureDisplayInfo.dbc' },
    { path: 'DBFilesClient\\Map.dbc', fields: 42, name: 'Map.dbc' },
    { path: 'DBFilesClient\\AreaTable.dbc', fields: 25, name: 'AreaTable.dbc' },
];
const MAX_EXE_BYTES = 64 * 1024 * 1024;

// ---- WoW.exe version resource --------------------------------------------------------------------------------------
/**
 * The file version of a PE image's VS_VERSION_INFO resource: {text: '1.12.1.5875', build: 5875}, or null when the bytes are
 * not a PE, have no version resource, or are cut short. Every offset is bounds-checked; this function never throws.
 */
export function parseExeVersion(bytes) {
    try {
        return readVersion(bytes);
    } catch {
        return null;
    }
}

function readVersion(bytes) {
    if (!(bytes instanceof Uint8Array) || bytes.length < 0x40 || bytes[0] !== 0x4D || bytes[1] !== 0x5A) {
        return null;
    }
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const inside = (offset, count) => Number.isSafeInteger(offset) && Number.isSafeInteger(count) && offset >= 0 && count >= 0 && offset + count <= bytes.length;
    const pe = view.getUint32(0x3C, true);
    if (!inside(pe, 24) || view.getUint32(pe, true) !== 0x00004550) {
        return null;
    }
    const sectionCount = view.getUint16(pe + 6, true);
    const optionalSize = view.getUint16(pe + 20, true);
    const optional = pe + 24;
    if (!inside(optional, optionalSize) || optionalSize < 2) {
        return null;
    }
    const magic = view.getUint16(optional, true);
    const directories = optional + (magic === 0x20B ? 112 : magic === 0x10B ? 96 : -1);
    if (directories < optional || !inside(directories + 16, 8) || directories + 24 > optional + optionalSize) {
        return null;
    }
    const resourceRva = view.getUint32(directories + 16, true);
    const resourceSize = view.getUint32(directories + 20, true);
    if (resourceRva === 0 || resourceSize === 0) {
        return null;
    }
    const sections = optional + optionalSize;
    if (!inside(sections, sectionCount * 40)) {
        return null;
    }
    const toOffset = (rva) => {
        for (let i = 0; i < sectionCount; i++) {
            const s = sections + (i * 40);
            const virtualSize = view.getUint32(s + 8, true);
            const virtualAddress = view.getUint32(s + 12, true);
            const rawSize = view.getUint32(s + 16, true);
            const rawPointer = view.getUint32(s + 20, true);
            if (rva >= virtualAddress && rva < virtualAddress + Math.max(virtualSize, rawSize)) {
                return rawPointer + (rva - virtualAddress);
            }
        }
        return -1;
    };
    const base = toOffset(resourceRva);
    if (base < 0 || !inside(base, 16)) {
        return null;
    }
    // Resource directory: type (RT_VERSION = 16) -> id -> language -> data entry. Only id entries are walked.
    const entry = (directory, wanted) => {
        if (!inside(directory, 16)) {
            return null;
        }
        const named = view.getUint16(directory + 12, true);
        const ids = view.getUint16(directory + 14, true);
        for (let i = 0; i < ids; i++) {
            const at = directory + 16 + ((named + i) * 8);
            if (!inside(at, 8)) {
                return null;
            }
            if (wanted === null || view.getUint32(at, true) === wanted) {
                const target = view.getUint32(at + 4, true);
                return { sub: (target & 0x80000000) !== 0, offset: target & 0x7FFFFFFF };
            }
        }
        return null;
    };
    const typeEntry = entry(base, 16);
    if (typeEntry === null || !typeEntry.sub) {
        return null;
    }
    const idEntry = entry(base + typeEntry.offset, null);
    if (idEntry === null || !idEntry.sub) {
        return null;
    }
    const languageEntry = entry(base + idEntry.offset, null);
    if (languageEntry === null || languageEntry.sub) {
        return null;
    }
    const dataEntry = base + languageEntry.offset;
    if (!inside(dataEntry, 16)) {
        return null;
    }
    const dataOffset = toOffset(view.getUint32(dataEntry, true));
    const dataSize = view.getUint32(dataEntry + 4, true);
    if (dataOffset < 0 || dataSize < 6 + 32 + 52 || !inside(dataOffset, dataSize)) {
        return null;
    }
    // VS_VERSIONINFO: wLength, wValueLength, wType, "VS_VERSION_INFO\0" (UTF-16), padding to 4, VS_FIXEDFILEINFO.
    const key = 'VS_VERSION_INFO\0';
    for (let i = 0; i < key.length; i++) {
        if (view.getUint16(dataOffset + 6 + (i * 2), true) !== key.charCodeAt(i)) {
            return null;
        }
    }
    const fixedAt = dataOffset + ((6 + (key.length * 2) + 3) & ~3);
    if (!inside(fixedAt, 52) || fixedAt + 52 > dataOffset + dataSize || view.getUint32(fixedAt, true) !== 0xFEEF04BD) {
        return null;
    }
    const ms = view.getUint32(fixedAt + 8, true);
    const ls = view.getUint32(fixedAt + 12, true);
    const build = ls & 0xFFFF;
    return { text: `${ms >>> 16}.${ms & 0xFFFF}.${ls >>> 16}.${build}`, build };
}

// ---- Folder ----------------------------------------------------------------------------------------------------------
async function readWhole(blob, limit) {
    if (blob.size > limit) {
        return null;
    }
    return new Uint8Array(await blob.arrayBuffer());
}

const failure = (errors, warnings, build = null, version = null) => ({ ok: false, errors, warnings, build, version });

/**
 * @param {object} folder from scope.js: {name, kind, hasData, archives: [{name, size, lastModified, blob}], dataFolders, exe}
 * @returns {Promise<{ok: boolean, errors: string[], warnings: string[], build: number|null, version: string|null}>}
 */
export async function validateClientFolder(folder) {
    const errors = [];
    const warnings = [];
    if (!folder.hasData) {
        return failure([ERROR_NO_DATA], warnings);
    }

    const present = new Set(folder.archives.map((a) => a.name.toLowerCase()));
    if ([...present].some((n) => LATER_CLIENTS.has(n))) {
        return failure([ERROR_BC], warnings);
    }
    for (const required of REQUIRED) {
        if (!present.has(`${required}.mpq`)) {
            errors.push(`Required archive ${required}.MPQ is missing from the Data folder`);
        }
    }
    if (errors.length > 0) {
        return failure(errors, warnings);
    }

    // Every archive's header and tables must parse; the archives are named as the folder spells them.
    const byName = new Map(folder.archives.map((a) => [a.name, a]));
    const opened = new Map();
    for (const name of loadOrder(folder.archives.map((a) => a.name))) {
        try {
            opened.set(name, await openArchive(byName.get(name).blob, { name }));
        } catch (error) {
            if (!(error instanceof ClientFsError)) {
                throw error;
            }
            errors.push(`Archive ${name} is damaged (header)`);
        }
    }
    if (errors.length > 0) {
        return failure(errors, warnings);
    }

    let exe = null;
    if (folder.exe) {
        const bytes = await readWhole(folder.exe.blob, MAX_EXE_BYTES);
        exe = bytes === null ? null : parseExeVersion(bytes);
    }

    // The DBC layouts decide whether this is the build the reader supports.
    const chain = createChain([...opened.values()]);
    let layoutProblem = null;
    for (const layout of LAYOUTS) {
        try {
            const bytes = await chain.read(layout.path);
            if (bytes === null) {
                layoutProblem = `${layout.name} is not in the client files`;
            } else {
                const table = parseDbc(bytes);
                if (table.fieldCount !== layout.fields) {
                    layoutProblem = `${layout.name} has ${table.fieldCount} fields instead of ${layout.fields}`;
                }
            }
        } catch (error) {
            if (!(error instanceof ClientFsError)) {
                throw error;
            }
            layoutProblem = `${layout.name} is unreadable (${error.message})`;
        }
        if (layoutProblem !== null) {
            break;
        }
    }

    if (layoutProblem !== null || (exe !== null && !(exe.text.startsWith('1.12.')))) {
        if (exe !== null && exe.build !== EXPECTED_BUILD) {
            return failure([`This is build ${exe.build} (expected ${EXPECTED_BUILD}); Atlas needs the 1.12.1 client`], warnings, exe.build, exe.text);
        }
        if (layoutProblem !== null && exe === null) {
            return failure([`This is build unknown (expected ${EXPECTED_BUILD}); Atlas needs the 1.12.1 client`], warnings);
        }
        return failure([`${layoutProblem}; the files do not match the data layouts of build ${EXPECTED_BUILD} (expected ${EXPECTED_BUILD} layout)`], warnings, exe?.build ?? null, exe?.text ?? null);
    }

    if (exe !== null && exe.text !== EXPECTED_VERSION) {
        warnings.push(`WoW.exe is version ${exe.text}; Atlas is built for ${EXPECTED_VERSION}. The data layouts match, so it will carry on.`);
    }
    if (folder.dataFolders.length > 0) {
        warnings.push(`Data contains a locale folder (${folder.dataFolders.join(', ')}). Localized installs are not verified yet; only the main archives are read.`);
    }
    return { ok: true, errors, warnings, build: exe?.build ?? EXPECTED_BUILD, version: exe?.text ?? null };
}

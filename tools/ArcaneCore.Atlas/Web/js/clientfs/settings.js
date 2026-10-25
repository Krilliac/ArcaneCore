// Pure helpers of the client-file settings panel: byte formatting and the cache-budget slider's bounds. No DOM, no storage.

const UNITS = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
export const BUDGET_STEP_BYTES = 1024 * 1024;
export const BUDGET_MIN_BYTES = 64 * 1024 * 1024;

/** 0 -> "0 B", 1536 -> "1.5 KiB". */
export function formatBytes(bytes) {
    let value = Math.max(0, Number(bytes) || 0);
    let unit = 0;
    while (value >= 1024 && unit < UNITS.length - 1) {
        value /= 1024;
        unit++;
    }
    const text = unit === 0 || value >= 100 ? String(Math.round(value)) : value.toFixed(1).replace(/\.0$/, '');
    return `${text} ${UNITS[unit]}`;
}

/**
 * The slider runs from 64 MiB to the browser's storage quota (never more); the current budget is clamped into it.
 * @param {{budget: number, quota: number|undefined}} input
 */
export function budgetSlider({ budget, quota }) {
    const max = Number.isFinite(quota) ? Math.max(0, Math.floor(quota)) : Math.max(budget, BUDGET_MIN_BYTES);
    const min = Math.min(BUDGET_MIN_BYTES, max);
    return { min, max, step: BUDGET_STEP_BYTES, value: Math.min(max, Math.max(min, budget)) };
}

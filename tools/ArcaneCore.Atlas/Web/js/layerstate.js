// Which layers are drawn, where and how, for a view and its compare mode.
//
// Compare modes (tools/ArcaneCore.Atlas/Core/ViewState.cs CompareMode):
//   server     server layers only                         reference  reference layers only
//   split      server left of the divider, reference right (one camera)
//   diff       diff layers over a dimmed server view
// World (tile grid) and Live layers belong to every mode. A layer the user ticked but the mode does not show is reported
// in `hiddenByMode`, so the layer rail can say why nothing appears.

import { groupSide, isLiveLayer } from './catalog.js';
import { layerStyle } from './view.js';

/** Opacity factor and forced mode for server layers under a diff overlay. */
export const DIM_FACTOR = 0.3;

/** 'server' | 'reference' | 'diff' | 'both' for a layer. */
export function layerSide(info) {
    return groupSide(info.group);
}

/** Regions a layer of this side is drawn in for the compare mode: subset of ['full', 'left', 'right']. */
export function regionsFor(side, compare) {
    switch (compare) {
        case 'reference':
            return side === 'reference' || side === 'both' ? ['full'] : [];
        case 'split': {
            const regions = [];
            if (side === 'server' || side === 'both') {
                regions.push('left');
            }
            if (side === 'reference' || side === 'both') {
                regions.push('right');
            }
            return regions;
        }
        case 'diff':
            return side === 'server' || side === 'diff' || side === 'both' ? ['full'] : [];
        default:
            return side === 'server' || side === 'both' ? ['full'] : [];
    }
}

/**
 * One entry per catalog layer, in catalog order:
 * {id, info, style, visible, regions, dim, mode, opacity, live, hiddenByMode}
 * `visible` = the style says visible AND some region draws it. `mode` and `opacity` are what the renderer should use.
 */
export function resolveLayers(view, infos) {
    return infos.map((info) => {
        const style = layerStyle(view, info.id, info);
        const side = layerSide(info);
        const regions = regionsFor(side, view.compare);
        const dim = view.compare === 'diff' && side === 'server';
        return {
            id: info.id,
            info,
            style,
            regions,
            visible: style.visible && regions.length > 0 && info.available !== false,
            dim,
            mode: dim ? 'wire' : style.mode,
            opacity: dim ? style.opacity * DIM_FACTOR : style.opacity,
            live: isLiveLayer(info.id),
            hiddenByMode: style.visible && regions.length === 0,
        };
    });
}

/** Layer ids whose tiles should be fetched for this view. */
export function wantedTileLayers(resolved) {
    return resolved.filter((l) => l.visible && !l.live && l.info.tiled !== false).map((l) => l.id);
}

/** Whether a layer's triangles write the depth buffer in the hidden-line pre-pass. */
export function writesDepth(layer) {
    if (layer.mode === 'xray' || layer.opacity <= 0) {
        return false;
    }
    if (layer.mode === 'fill') {
        return layer.opacity >= 0.99;
    }
    return true;
}

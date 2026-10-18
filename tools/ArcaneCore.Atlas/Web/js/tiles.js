// Tile and grid math. A port of tools/ArcaneCore.Atlas/Core/WorldGrid.cs, which itself delegates to the server:
//   ref: src/ArcaneCore.Game/Maps/Grid/GridDefines.cs  SizeOfGrids = 533.33333f, MaxNumberOfGrids = 64, MapResolution = 128,
//        MaxNumberOfCells = 16
//   ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs  TileOf(x, y): (int)(32 - x / GridSize), FileName,
//        TileOf(GridCoord): tile = 63 - grid per axis, GetAreaFlag / GetHeightFromFloat cell and square arithmetic
//   ref: src/ArcaneCore.Game/Maps/Collision/VMaps/VMapFormat.cs  TileFileName (tile Y first)
//   ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshFormat.cs  TileFileName
// The server computes in float32; Math.fround reproduces each intermediate so tile edges fall where the server's do.

export const TILES_PER_SIDE = 64;
export const MAP_RESOLUTION = 128;
export const CELLS_PER_TILE_SIDE = 16;

/** SizeOfGrids as the server's float (the constant widened to double, exactly like WorldGrid.TileSize). */
export const TILE_SIZE = Math.fround(533.33333);
export const SQUARE_SIZE = TILE_SIZE / MAP_RESOLUTION;
export const CELL_SIZE = TILE_SIZE / CELLS_PER_TILE_SIDE;
export const GLOBAL_INDEX = -1;

const f32 = Math.fround;

/** The tile holding a world position, or null outside the 64x64 grid (TerrainTile.TileOf). */
export function tileOf(x, y) {
    const fx = f32(x);
    const fy = f32(y);
    if (!Number.isFinite(fx) || !Number.isFinite(fy)) {
        return null;
    }
    const tx = Math.trunc(f32(32 - f32(fx / TILE_SIZE)));
    const ty = Math.trunc(f32(32 - f32(fy / TILE_SIZE)));
    return inGrid(tx, ty) ? { x: tx, y: ty } : null;
}

export function inGrid(tx, ty) {
    return tx >= 0 && tx < TILES_PER_SIDE && ty >= 0 && ty < TILES_PER_SIDE;
}

export const isGlobal = (tx, ty) => tx === GLOBAL_INDEX && ty === GLOBAL_INDEX;

/** A real grid tile or the global pseudo-tile (TileKey.IsValid). */
export const isValidTile = (tx, ty) => isGlobal(tx, ty) || inGrid(tx, ty);

/** Corner with the largest world X and Y (the .map file's first sample); rows advance toward -X, columns toward -Y. */
export function tileOrigin(tx, ty) {
    if (isGlobal(tx, ty)) {
        return [0, 0, 0];
    }
    return [(32 - tx) * TILE_SIZE, (32 - ty) * TILE_SIZE, 0];
}

/** World extent of a tile: X in (minX, maxX], Y in (minY, maxY]. */
export function tileBounds(tx, ty) {
    const [ox, oy] = tileOrigin(tx, ty);
    return { minX: ox - TILE_SIZE, maxX: ox, minY: oy - TILE_SIZE, maxY: oy };
}

export function tileCentre(tx, ty) {
    const [ox, oy] = tileOrigin(tx, ty);
    return [ox - TILE_SIZE / 2, oy - TILE_SIZE / 2];
}

const pad = (value, width) => String(value).padStart(width, '0');

/** Server terrain file, e.g. 0003248.map for map 0, tile 32, 48. */
export const mapFileName = (map, tx, ty) => `${pad(map, 3)}${pad(tx, 2)}${pad(ty, 2)}.map`;

/** Server navmesh tile file. */
export const navTileFileName = (map, tx, ty) => `${pad(map, 3)}${pad(tx, 2)}${pad(ty, 2)}.mmtile`;

/** Server vmap tile file; tile Y comes first in the name. */
export const vmapTileFileName = (map, tx, ty) => `${pad(map, 3)}_${pad(ty, 2)}_${pad(tx, 2)}.vmtile`;

/** Tiles within `radius` tiles (Chebyshev) of the tile holding a point; clipped to the grid. */
export function around(x, y, radius) {
    const centre = tileOf(x, y);
    if (!centre) {
        return [];
    }
    const out = [];
    for (let dx = -radius; dx <= radius; dx++) {
        for (let dy = -radius; dy <= radius; dy++) {
            const tx = centre.x + dx;
            const ty = centre.y + dy;
            if (inGrid(tx, ty)) {
                out.push({ x: tx, y: ty });
            }
        }
    }
    return out;
}

/**
 * The tiles to hold for a focus tile: the grid tiles within `radius`, nearest first, so the tile under the camera
 * is fetched before its neighbours.
 */
export function wantedTiles(centre, radius) {
    const out = [];
    for (let dx = -radius; dx <= radius; dx++) {
        for (let dy = -radius; dy <= radius; dy++) {
            const tx = centre.x + dx;
            const ty = centre.y + dy;
            if (inGrid(tx, ty)) {
                out.push({ x: tx, y: ty, ring: Math.max(Math.abs(dx), Math.abs(dy)), d2: dx * dx + dy * dy });
            }
        }
    }
    out.sort((a, b) => a.d2 - b.d2 || a.x - b.x || a.y - b.y);
    return out;
}

/** The tile of a world-server grid pair (TerrainTile.TileOf(GridCoord): 63 - grid on each axis). */
export const tileOfGrid = (gx, gy) => ({ x: TILES_PER_SIDE - 1 - gx, y: TILES_PER_SIDE - 1 - gy });

/** The 16x16 cell inside a tile that holds the area flag (TerrainTile.GetAreaFlag: ((int)(16 * (32 - x / G))) & 15). */
export function cellOf(x, y) {
    const cx = Math.trunc(f32(CELLS_PER_TILE_SIDE * f32(32 - f32(f32(x) / TILE_SIZE))));
    const cy = Math.trunc(f32(CELLS_PER_TILE_SIDE * f32(32 - f32(f32(y) / TILE_SIZE))));
    return { x: cx & (CELLS_PER_TILE_SIDE - 1), y: cy & (CELLS_PER_TILE_SIDE - 1) };
}

/** The 128x128 height square inside a tile (TerrainTile.GetHeightFromFloat: ((int)(128 * (32 - x / G))) & 127). */
export function squareOf(x, y) {
    const sx = Math.trunc(f32(MAP_RESOLUTION * f32(32 - f32(f32(x) / TILE_SIZE))));
    const sy = Math.trunc(f32(MAP_RESOLUTION * f32(32 - f32(f32(y) / TILE_SIZE))));
    return { x: sx & (MAP_RESOLUTION - 1), y: sy & (MAP_RESOLUTION - 1) };
}

export const tileKey = (layer, map, tx, ty) => `${layer}|${map}|${tx}|${ty}`;

/** Everything the cursor readout shows for a world position. */
export function describePosition(map, x, y) {
    const tile = tileOf(x, y);
    if (!tile) {
        return { tile: null, cell: null, square: null, files: null };
    }
    return {
        tile,
        cell: cellOf(x, y),
        square: squareOf(x, y),
        files: {
            map: mapFileName(map, tile.x, tile.y),
            vmap: vmapTileFileName(map, tile.x, tile.y),
            mmap: navTileFileName(map, tile.x, tile.y),
        },
    };
}

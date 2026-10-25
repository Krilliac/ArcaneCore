// A recording stand-in for the WebGL2 calls the TextureStore makes, so its memory accounting can be tested without a GPU. It models
// only what the store uses: texture objects, uploads (plain and compressed), parameters, and the S3TC extension query.

const S3TC = {
    COMPRESSED_RGB_S3TC_DXT1_EXT: 0x83F0,
    COMPRESSED_RGBA_S3TC_DXT1_EXT: 0x83F1,
    COMPRESSED_RGBA_S3TC_DXT3_EXT: 0x83F2,
    COMPRESSED_RGBA_S3TC_DXT5_EXT: 0x83F3,
};

export class FakeGl {
    /** @param {{s3tc?: boolean}} [options] */
    constructor({ s3tc = false } = {}) {
        this.s3tc = s3tc;
        this.TEXTURE_2D = 0x0DE1;
        this.TEXTURE_2D_ARRAY = 0x8C1A;
        this.READ_FRAMEBUFFER = 0x8CA8;
        this.COLOR_ATTACHMENT0 = 0x8CE0;
        this.MAX_ARRAY_TEXTURE_LAYERS = 0x88FF;
        this.maxArrayLayers = 2048;
        this.storage3D = [];
        this.layerUploads = [];
        this.layerCopies = [];
        this.framebuffersCreated = 0;
        this.framebuffersDeleted = 0;
        this.RGBA = 0x1908;
        this.UNSIGNED_BYTE = 0x1401;
        this.live = new Set();
        this.created = 0;
        this.deleted = 0;
        this.uploads = [];
        this.compressedUploads = [];
        this.parameters = [];
        this.mipmapsGenerated = 0;
        this.nextId = 1;
        this.bound = null;
        this.extensionQueries = [];
    }

    createTexture() {
        const texture = { id: this.nextId++, levels: [] };
        this.live.add(texture);
        this.created++;
        return texture;
    }

    deleteTexture(texture) {
        if (this.live.delete(texture)) {
            this.deleted++;
        }
    }

    bindTexture(target, texture) {
        this.bound = texture;
    }

    texParameteri(target, name, value) {
        this.parameters.push([this.bound?.id ?? null, name, value]);
    }

    pixelStorei() {}

    texImage2D(target, level, internalFormat, width, height, border, format, type, data) {
        this.uploads.push({ texture: this.bound, level, width, height, bytes: data?.byteLength ?? 0 });
        this.bound?.levels.push({ level, width, height, compressed: false });
    }

    compressedTexImage2D(target, level, internalFormat, width, height, border, data) {
        this.compressedUploads.push({ texture: this.bound, level, internalFormat, width, height, bytes: data?.byteLength ?? 0 });
        this.bound?.levels.push({ level, width, height, compressed: true });
    }

    generateMipmap() {
        this.mipmapsGenerated++;
    }

    getParameter(name) {
        return name === this.MAX_ARRAY_TEXTURE_LAYERS ? this.maxArrayLayers : 0;
    }

    texStorage3D(target, levels, internalFormat, width, height, depth) {
        this.storage3D.push({ texture: this.bound, levels, width, height, depth });
    }

    texSubImage3D(target, level, x, y, layer, width, height, depth, format, type, data) {
        this.layerUploads.push({ texture: this.bound, level, layer, width, height, bytes: data?.byteLength ?? 0 });
    }

    compressedTexSubImage3D(target, level, x, y, layer, width, height, depth, internalFormat, data) {
        this.layerUploads.push({ texture: this.bound, level, layer, width, height, bytes: data?.byteLength ?? 0, compressed: true });
    }

    createFramebuffer() {
        this.framebuffersCreated++;
        return { id: this.nextId++ };
    }

    deleteFramebuffer() {
        this.framebuffersDeleted++;
    }

    bindFramebuffer() {}

    framebufferTextureLayer(target, attachment, texture, level, layer) {
        this.readLayer = { texture, layer };
    }

    copyTexSubImage3D(target, level, x, y, layer) {
        this.layerCopies.push({ from: this.readLayer, to: this.bound, layer });
    }

    getExtension(name) {
        this.extensionQueries.push(name);
        return name === 'WEBGL_compressed_texture_s3tc' && this.s3tc ? { ...S3TC } : null;
    }

    getError() {
        return 0;
    }
}

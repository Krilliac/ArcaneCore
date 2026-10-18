// GLSL ES 3.00 sources. Every vertex stage works camera-relative: positions are offsets from the chunk origin and
// uRel = chunkOrigin - eye (computed in double on the CPU), uView is rotation only, so float precision is spent near the
// camera. Palette colours come from a per-chunk lookup texture (256 wide, RGBA8; alpha 0 = no colour of its own).

const HEADER = '#version 300 es\nprecision highp float;\nprecision highp int;\nprecision highp sampler2D;\n';

const COMMON_UNIFORMS = `
uniform mat4 uView;
uniform mat4 uProj;
uniform vec3 uRel;
`;

const LUT_FETCH = `
vec4 lutColor(sampler2D lut, uint pal) {
    return texelFetch(lut, ivec2(int(pal & 255u), int(pal >> 8u)), 0);
}
`;

/** Triangles, position only: the hidden-line depth pre-pass. */
export const DEPTH = {
    vertex: `${HEADER}
layout(location = 0) in vec3 aPos;
${COMMON_UNIFORMS}
void main() {
    gl_Position = uProj * (uView * vec4(aPos + uRel, 1.0));
}`,
    fragment: `${HEADER}
out vec4 outColor;
void main() {
    outColor = vec4(0.0);
}`,
};

/** Flat-shaded triangles, coloured per attribute. */
export const FILL = {
    vertex: `${HEADER}
layout(location = 0) in vec3 aPos;
layout(location = 1) in uint aPal;
${COMMON_UNIFORMS}
uniform sampler2D uLut;
uniform vec3 uColor;
uniform bool uByAttribute;
out vec3 vView;
flat out vec3 vColor;
${LUT_FETCH}
void main() {
    vec4 v = uView * vec4(aPos + uRel, 1.0);
    vView = v.xyz;
    gl_Position = uProj * v;
    vec3 c = uColor;
    if (uByAttribute) {
        vec4 l = lutColor(uLut, aPal);
        if (l.a > 0.5) {
            c = l.rgb;
        }
    }
    vColor = c;
}`,
    fragment: `${HEADER}
in vec3 vView;
flat in vec3 vColor;
uniform vec3 uLight;
uniform float uAlpha;
out vec4 outColor;
void main() {
    vec3 n = cross(dFdx(vView), dFdy(vView));
    float l = length(n);
    n = l > 0.0 ? n / l : vec3(0.0, 0.0, 1.0);
    float shade = 0.40 + 0.60 * abs(dot(n, uLight));
    outColor = vec4(vColor * shade, uAlpha);
}`,
};

/** Triangles into an integer target: (chunk slot, triangle number) for cursor picking. */
export const PICK = {
    vertex: `${HEADER}
layout(location = 0) in vec3 aPos;
${COMMON_UNIFORMS}
flat out uint vTriangle;
void main() {
    vTriangle = uint(gl_VertexID / 3);
    gl_Position = uProj * (uView * vec4(aPos + uRel, 1.0));
}`,
    fragment: `${HEADER}
flat in uint vTriangle;
uniform uint uSlot;
layout(location = 0) out uvec4 outId;
void main() {
    outId = uvec4(uSlot, vTriangle, 0u, 1u);
}`,
};

/**
 * Thick screen-space lines: one instance per segment, six vertices forming a quad around it. Colour modes:
 * 0 the layer colour, 1 the chunk palette, 2 a packed RGBA8 word per instance. Segments crossing the near plane are
 * clipped to it. uBias pulls the line toward the camera in view space (relative to distance, plus an absolute part)
 * so edges win the depth test against the fill they lie on.
 */
export const LINE = {
    vertex: `${HEADER}
layout(location = 0) in vec3 aA;
layout(location = 1) in vec3 aB;
layout(location = 2) in uint aColor;
${COMMON_UNIFORMS}
uniform sampler2D uLut;
uniform vec4 uStyle;
uniform int uColorMode;
uniform vec2 uViewport;
uniform float uWidth;
uniform vec2 uBias;
uniform bool uOrtho;
out vec4 vColor;
out float vSide;
out float vAlong;
${LUT_FETCH}
vec4 toClip(vec3 p) {
    vec4 v = uView * vec4(p + uRel, 1.0);
    v.z += uBias.x * (uOrtho ? 1.0 : -v.z) + uBias.y;
    return uProj * v;
}
void main() {
    vec4 a = toClip(aA);
    vec4 b = toClip(aB);
    float da = a.z + a.w;
    float db = b.z + b.w;
    if (da < 0.0 && db < 0.0) {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        return;
    }
    if (da < 0.0) {
        a = mix(a, b, da / (da - db));
    } else if (db < 0.0) {
        b = mix(a, b, da / (da - db));
    }
    vec2 sa = a.xy / a.w * uViewport * 0.5;
    vec2 sb = b.xy / b.w * uViewport * 0.5;
    vec2 d = sb - sa;
    float len = length(d);
    vec2 dir = len > 1e-4 ? d / len : vec2(1.0, 0.0);
    vec2 nrm = vec2(-dir.y, dir.x);
    int vi = gl_VertexID;
    float tx = (vi == 1 || vi == 2 || vi == 4) ? 1.0 : 0.0;
    float ty = (vi == 2 || vi == 4 || vi == 5) ? 1.0 : -1.0;
    float half_ = uWidth * 0.5 + 0.5;
    vec4 p = mix(a, b, tx);
    float cap = tx > 0.5 ? half_ : -half_;
    p.xy += (dir * cap + nrm * ty * half_) * 2.0 / uViewport * p.w;
    gl_Position = p;
    vSide = ty * half_;
    vAlong = (tx > 0.5 ? len : 0.0) + cap;
    vec4 c = vec4(uStyle.rgb, 1.0);
    if (uColorMode == 1) {
        vec4 l = lutColor(uLut, aColor);
        if (l.a > 0.5) {
            c.rgb = l.rgb;
        }
    } else if (uColorMode == 2) {
        c = vec4(float(aColor & 255u), float((aColor >> 8u) & 255u), float((aColor >> 16u) & 255u), float(aColor >> 24u)) / 255.0;
    }
    vColor = vec4(c.rgb, c.a * uStyle.a);
}`,
    fragment: `${HEADER}
in vec4 vColor;
in float vSide;
in float vAlong;
uniform float uWidth;
uniform float uDash;
out vec4 outColor;
void main() {
    float coverage = clamp(uWidth * 0.5 + 0.5 - abs(vSide), 0.0, 1.0);
    if (uDash > 0.0 && mod(vAlong, uDash) > uDash * 0.55) {
        discard;
    }
    outColor = vec4(vColor.rgb, vColor.a * coverage);
}`,
};

/**
 * Screen-space markers (instanced quads with a signed-distance shape): used for tile points and for live entities.
 * Shapes: 0 circle, 1 diamond, 2 square, 3 ring. Colour: palette (mode 1), packed per instance (mode 2) or the style.
 */
export const MARKER = {
    vertex: `${HEADER}
layout(location = 0) in vec3 aPos;
layout(location = 1) in uint aColor;
layout(location = 2) in float aSize;
layout(location = 3) in float aShape;
${COMMON_UNIFORMS}
uniform sampler2D uLut;
uniform vec4 uStyle;
uniform int uColorMode;
uniform vec2 uViewport;
uniform float uScale;
uniform vec2 uBias;
uniform bool uOrtho;
out vec4 vColor;
out vec2 vCorner;
out float vHalf;
flat out int vShape;
${LUT_FETCH}
void main() {
    vec4 v = uView * vec4(aPos + uRel, 1.0);
    v.z += uBias.x * (uOrtho ? 1.0 : -v.z) + uBias.y;
    vec4 c = uProj * v;
    if (c.w <= 0.0 || c.z < -c.w) {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        return;
    }
    int vi = gl_VertexID;
    vec2 corner = vec2((vi == 1 || vi == 2 || vi == 4) ? 1.0 : -1.0, (vi == 2 || vi == 4 || vi == 5) ? 1.0 : -1.0);
    float half_ = aSize * uScale * 0.5;
    float reach = half_ + 1.0;
    c.xy += corner * reach * 2.0 / uViewport * c.w;
    gl_Position = c;
    vCorner = corner * reach;
    vHalf = half_;
    vShape = int(aShape + 0.5);
    vec4 col = vec4(uStyle.rgb, 1.0);
    if (uColorMode == 1) {
        vec4 l = lutColor(uLut, aColor);
        if (l.a > 0.5) {
            col.rgb = l.rgb;
        }
    } else if (uColorMode == 2) {
        col = vec4(float(aColor & 255u), float((aColor >> 8u) & 255u), float((aColor >> 16u) & 255u), float(aColor >> 24u)) / 255.0;
    }
    vColor = vec4(col.rgb, col.a * uStyle.a);
}`,
    fragment: `${HEADER}
in vec4 vColor;
in vec2 vCorner;
in float vHalf;
flat in int vShape;
out vec4 outColor;
void main() {
    float d;
    if (vShape == 1) {
        d = (abs(vCorner.x) + abs(vCorner.y)) - vHalf * 1.25;
    } else if (vShape == 2) {
        d = max(abs(vCorner.x), abs(vCorner.y)) - vHalf;
    } else if (vShape == 3) {
        d = abs(length(vCorner) - vHalf * 0.85) - max(vHalf * 0.15, 0.9);
    } else {
        d = length(vCorner) - vHalf;
    }
    float coverage = clamp(0.5 - d, 0.0, 1.0);
    if (coverage <= 0.0) {
        discard;
    }
    float inner = vShape == 3 ? 1.0 : clamp(0.5 - (d + 1.2), 0.0, 1.0);
    vec3 rgb = mix(vec3(0.04, 0.07, 0.09), vColor.rgb, inner);
    outColor = vec4(rgb, vColor.a * coverage);
}`,
};

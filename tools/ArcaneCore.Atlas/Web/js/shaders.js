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

/** Shaded triangles, coloured per attribute or layer style, with terrain/model shading modes, contours, liquid & fog. */
export const FILL = {
    vertex: `${HEADER}
layout(location = 0) in vec3 aPos;
layout(location = 1) in uint aPal;
${COMMON_UNIFORMS}
uniform vec3 uOrigin;
uniform sampler2D uLut;
uniform vec3 uColor;
uniform bool uByAttribute;
out vec3 vWorld;
out vec3 vRelPos;
out vec3 vView;
flat out vec3 vColor;
${LUT_FETCH}
void main() {
    vec3 relPos = aPos + uRel;
    vec4 v = uView * vec4(relPos, 1.0);
    vWorld = aPos + uOrigin;
    vRelPos = relPos;
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
in vec3 vWorld;
in vec3 vRelPos;
in vec3 vView;
flat in vec3 vColor;
uniform vec3 uLight;
uniform float uAlpha;
uniform int uShadeMode;
uniform int uContours;
uniform int uFog;
uniform int uMode;
uniform vec3 uSunDir;
uniform int uIsLiquid;
out vec4 outColor;

vec3 hypsometricTint(float z) {
    float t = clamp((z + 40.0) / 440.0, 0.0, 1.0);
    vec3 low = vec3(0.32, 0.52, 0.36);
    vec3 mid = vec3(0.68, 0.63, 0.44);
    vec3 high = vec3(0.56, 0.50, 0.45);
    vec3 peak = vec3(0.88, 0.91, 0.94);
    if (t < 0.35) {
        return mix(low, mid, t / 0.35);
    } else if (t < 0.72) {
        return mix(mid, high, (t - 0.35) / 0.37);
    }
    return mix(high, peak, (t - 0.72) / 0.28);
}

void main() {
    vec3 n = cross(dFdx(vView), dFdy(vView));
    float l = length(n);
    n = l > 0.0 ? n / l : vec3(0.0, 0.0, 1.0);
    if (n.z < 0.0) {
        n = -n;
    }

    vec3 worldN = cross(dFdx(vRelPos), dFdy(vRelPos));
    float wl = length(worldN);
    worldN = wl > 0.0 ? worldN / wl : vec3(0.0, 0.0, 1.0);
    if (worldN.z < 0.0) {
        worldN = -worldN;
    }

    vec3 sunDir = length(uSunDir) > 1e-4 ? normalize(uSunDir) : normalize(vec3(-0.45, -0.35, 0.82));
    float ndotl = max(dot(worldN, sunDir), 0.0);
    float hemi = 0.5 + 0.5 * clamp(worldN.z, -1.0, 1.0);
    vec3 viewDir = length(vView) > 1e-4 ? normalize(-vView) : vec3(0.0, 0.0, 1.0);
    float rim = pow(1.0 - max(dot(n, viewDir), 0.0), 2.5);

    vec3 rgb = vColor;
    if (uShadeMode == 1) {
        vec3 relief = mix(vColor, hypsometricTint(vWorld.z), 0.65);
        float slope = 1.0 - clamp(worldN.z, 0.0, 1.0);
        vec3 skyAmb = mix(vec3(0.22, 0.25, 0.30), vec3(0.42, 0.46, 0.52), hemi);
        float sunTerm = 0.58 * ndotl + 0.16 * max(dot(n, uLight), 0.0);
        rgb = relief * (skyAmb + sunTerm) * (1.0 - 0.28 * slope) + 0.05 * rim;
    } else if (uShadeMode == 2) {
        rgb = vec3(0.5 + 0.5 * worldN.x, 0.5 + 0.5 * worldN.y, clamp(worldN.z, 0.0, 1.0));
        rgb *= 0.82 + 0.18 * ndotl;
    } else if (uShadeMode == 3) {
        float nz = clamp(worldN.z, 0.0, 1.0);
        vec3 flatCol = vec3(0.20, 0.74, 0.52);
        vec3 gentleCol = vec3(0.46, 0.80, 0.34);
        vec3 warnCol = vec3(0.96, 0.65, 0.14);
        vec3 cliffCol = vec3(0.90, 0.26, 0.28);
        vec3 heat;
        if (nz >= 0.6427876) {
            float kt = clamp((nz - 0.6427876) / (1.0 - 0.6427876), 0.0, 1.0);
            heat = mix(gentleCol, flatCol, kt);
        } else if (nz >= 0.5) {
            float kt = clamp((nz - 0.5) / (0.6427876 - 0.5), 0.0, 1.0);
            heat = mix(warnCol, gentleCol, kt);
        } else {
            float kt = clamp(nz / 0.5, 0.0, 1.0);
            heat = mix(vec3(0.58, 0.12, 0.16), cliffCol, kt);
        }
        float light = 0.55 + 0.35 * ndotl + 0.10 * hemi;
        rgb = heat * light;
    } else {
        float amb = mix(0.28, 0.46, hemi);
        float diff = 0.42 * ndotl + 0.18 * abs(dot(n, uLight));
        rgb = vColor * (amb + diff) + vec3(0.04) * rim;
    }

    if (uIsLiquid == 1 && uShadeMode != 2 && uShadeMode != 3) {
        vec3 eyeWorld = length(vRelPos) > 1e-4 ? normalize(-vRelPos) : vec3(0.0, 0.0, 1.0);
        float fresnel = pow(1.0 - max(dot(worldN, eyeWorld), 0.0), 3.0);
        vec3 halfVec = normalize(sunDir + eyeWorld);
        float spec = pow(max(dot(worldN, halfVec), 0.0), 48.0);
        float ripple = 0.025 * sin(vWorld.x * 0.45 + vWorld.y * 0.35) * cos(vWorld.y * 0.40 - vWorld.x * 0.25);
        rgb = mix(rgb + ripple, vec3(0.55, 0.78, 0.96), 0.28 * fresnel) + vec3(0.35, 0.42, 0.48) * spec;
    }

    if (uContours == 1 && worldN.z > 0.15) {
        float fw = max(fwidth(vWorld.z), 1e-4);
        float d10 = abs(fract(vWorld.z / 10.0 + 0.5) - 0.5) * 10.0 / fw;
        float d50 = abs(fract(vWorld.z / 50.0 + 0.5) - 0.5) * 50.0 / fw;
        float minorLine = 1.0 - smoothstep(0.0, 1.25, d10);
        float majorLine = 1.0 - smoothstep(0.0, 1.65, d50);
        float fade = 1.0 - smoothstep(1.5, 4.5, fw);
        float lineAlpha = clamp(max(minorLine * 0.22, majorLine * 0.45) * fade, 0.0, 0.55);
        rgb = mix(rgb, vec3(0.06, 0.09, 0.12), lineAlpha);
    }

    if (uFog == 1 && uMode == 1) {
        float dist = length(vView);
        float fogFactor = clamp(1.0 - exp(-max(dist - 120.0, 0.0) * 0.00055), 0.0, 0.52);
        vec3 fogColor = vec3(0.09, 0.14, 0.18);
        rgb = mix(rgb, fogColor, fogFactor);
    }

    outColor = vec4(clamp(rgb, 0.0, 1.0), uAlpha);
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

/**
 * Textured triangles of an ATG1 version 2 chunk (the `generic` material, design section 4): one 2D texture at uv0, optionally
 * multiplied by the vertex colour, lit with the same sun/hemisphere terms as FILL so a textured layer sits next to an untextured
 * one in a split or diff view. Vertices are expanded per triangle: position (offset from the chunk origin), uv0, colour0 (RGBA8).
 */
export const MATERIAL = {
    vertex: `${HEADER}
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aColor;
${COMMON_UNIFORMS}
out vec3 vRelPos;
out vec3 vView;
out vec2 vUv;
out vec4 vColor;
void main() {
    vec3 relPos = aPos + uRel;
    vec4 v = uView * vec4(relPos, 1.0);
    vRelPos = relPos;
    vView = v.xyz;
    vUv = aUv;
    vColor = aColor;
    gl_Position = uProj * v;
}`,
    fragment: `${HEADER}
in vec3 vRelPos;
in vec3 vView;
in vec2 vUv;
in vec4 vColor;
uniform sampler2D uTexture;
uniform vec3 uLight;
uniform vec3 uSunDir;
uniform vec3 uFlat;
uniform float uAlpha;
uniform float uAlphaRef;
uniform bool uTextured;
uniform bool uUnlit;
uniform bool uAlphaKey;
uniform bool uIgnoreAlpha;
uniform bool uUseColor0;
uniform bool uFogOn;
uniform int uMode;
out vec4 outColor;

void main() {
    vec4 texel = uTextured ? texture(uTexture, vUv) : vec4(uFlat, 1.0);
    if (uUseColor0) {
        texel *= vColor;
    }
    if (uAlphaKey && texel.a < uAlphaRef) {
        discard;
    }
    float alpha = (uIgnoreAlpha ? 1.0 : texel.a) * uAlpha;
    vec3 rgb = texel.rgb;
    if (!uUnlit) {
        vec3 n = cross(dFdx(vView), dFdy(vView));
        float l = length(n);
        n = l > 0.0 ? n / l : vec3(0.0, 0.0, 1.0);
        if (n.z < 0.0) {
            n = -n;
        }
        vec3 worldN = cross(dFdx(vRelPos), dFdy(vRelPos));
        float wl = length(worldN);
        worldN = wl > 0.0 ? worldN / wl : vec3(0.0, 0.0, 1.0);
        if (worldN.z < 0.0) {
            worldN = -worldN;
        }
        vec3 sunDir = length(uSunDir) > 1e-4 ? normalize(uSunDir) : normalize(vec3(-0.45, -0.35, 0.82));
        float ndotl = max(dot(worldN, sunDir), 0.0);
        float hemi = 0.5 + 0.5 * clamp(worldN.z, -1.0, 1.0);
        float amb = mix(0.28, 0.46, hemi);
        float diff = 0.42 * ndotl + 0.18 * abs(dot(n, uLight));
        rgb *= amb + diff;
    }
    if (uFogOn && uMode == 1) {
        float dist = length(vView);
        float fogFactor = clamp(1.0 - exp(-max(dist - 120.0, 0.0) * 0.00055), 0.0, 0.52);
        rgb = mix(rgb, vec3(0.09, 0.14, 0.18), fogFactor);
    }
    outColor = vec4(clamp(rgb, 0.0, 1.0), alpha);
}`,
};

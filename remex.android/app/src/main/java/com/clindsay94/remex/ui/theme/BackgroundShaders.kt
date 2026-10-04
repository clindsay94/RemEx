package com.clindsay94.remex.ui.theme

import com.clindsay94.remex.data.BackgroundStyles

/**
 * AGSL for the three animated backgrounds (RemEx-pp4cm.17). minSdk is 34, so `RuntimeShader`
 * (API 33) is always there and there is no older-device fallback to carry.
 *
 * Every shader takes the same uniforms so one setter serves all of them:
 *  - `uSize`  the layer size in pixels
 *  - `uScale` pixels per dp, so shapes keep their size on any screen density
 *  - `uTime`  seconds on the style's clock; a paused background simply stops advancing it
 *  - `uA`, `uB`, `uC` the style's colour roles as sRGB (see `BackgroundPalette.roles`)
 *
 * Each returns a premultiplied colour whose alpha is the shape's own coverage; the layer's overall
 * opacity (the intensity slider, capped by `BackgroundPalette.maxAlpha`) is applied by the draw call.
 */
internal object BackgroundShaders {

    private const val HEADER = """
        uniform float2 uSize;
        uniform float uScale;
        uniform float uTime;
        uniform float3 uA;
        uniform float3 uB;
        uniform float3 uC;
    """

    /** Three slow, overlapping ribbons of colour drifting across the screen. */
    private const val AURORA = HEADER + """
        half4 main(float2 fc) {
            float2 uv = fc / uSize;
            float t = uTime * 0.07;
            float c1 = 0.26 + 0.10 * sin(uv.x * 2.6 + t * 2.1) + 0.05 * sin(uv.x * 5.3 - t * 1.3);
            float c2 = 0.52 + 0.12 * sin(uv.x * 2.1 - t * 1.7 + 1.9) + 0.05 * sin(uv.x * 4.7 + t * 1.1);
            float c3 = 0.78 + 0.09 * sin(uv.x * 3.1 + t * 1.5 + 4.0) + 0.04 * sin(uv.x * 6.1 - t * 0.9);
            float d1 = (uv.y - c1) / 0.17;
            float d2 = (uv.y - c2) / 0.19;
            float d3 = (uv.y - c3) / 0.16;
            float b1 = exp(-d1 * d1);
            float b2 = exp(-d2 * d2);
            float b3 = exp(-d3 * d3);
            float s = b1 + b2 + b3;
            float3 col = (uA * b1 + uB * b2 + uC * b3) / max(s, 0.0001);
            float a = clamp(s, 0.0, 1.0) * 0.85;
            return half4(half3(col * a), half(a));
        }
    """

    /** Four soft colour blobs wandering over the screen and blending into one another. */
    private const val MESH = HEADER + """
        half4 main(float2 fc) {
            float2 uv = fc / uSize;
            float aspect = uSize.x / uSize.y;
            float t = uTime * 0.06;
            float2 p0 = float2(0.50 + 0.34 * sin(t * 1.3 + 0.2), 0.50 + 0.30 * sin(t * 1.1 + 1.4));
            float2 p1 = float2(0.50 + 0.36 * sin(t * 0.9 + 2.6), 0.50 + 0.34 * sin(t * 1.4 + 3.1));
            float2 p2 = float2(0.50 + 0.32 * sin(t * 1.2 + 4.4), 0.50 + 0.36 * sin(t * 0.8 + 5.0));
            float2 p3 = float2(0.50 + 0.38 * sin(t * 0.7 + 0.9), 0.50 + 0.32 * sin(t * 1.0 + 2.2));
            float2 q = float2(uv.x * aspect, uv.y);
            float w0 = 1.0 / (0.02 + dot(q - float2(p0.x * aspect, p0.y), q - float2(p0.x * aspect, p0.y)));
            float w1 = 1.0 / (0.02 + dot(q - float2(p1.x * aspect, p1.y), q - float2(p1.x * aspect, p1.y)));
            float w2 = 1.0 / (0.02 + dot(q - float2(p2.x * aspect, p2.y), q - float2(p2.x * aspect, p2.y)));
            float w3 = 1.0 / (0.02 + dot(q - float2(p3.x * aspect, p3.y), q - float2(p3.x * aspect, p3.y)));
            float s = w0 + w1 + w2 + w3;
            float3 col = (uA * w0 + uB * w1 + uC * w2 + uA * w3) / s;
            float a = 0.8;
            return half4(half3(col * a), half(a));
        }
    """

    /** Two layers of faint stars that twinkle and drift slowly upward. */
    private const val STARFIELD = HEADER + """
        float hash21(float2 p) {
            return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
        }
        half4 main(float2 fc) {
            float a = 0.0;
            float3 col = uA;
            for (int layer = 0; layer < 2; layer++) {
                float l = float(layer);
                float cell = (46.0 + 40.0 * l) * uScale;
                float2 q = (fc + float2(0.0, uTime * (5.0 + 4.0 * l) * uScale)) / cell;
                float2 id = floor(q);
                float2 f = fract(q);
                float h = hash21(id + l * 17.0);
                float2 pos = float2(hash21(id + 1.3), hash21(id + 7.1)) * 0.6 + 0.2;
                float d = length(f - pos) * cell / uScale;
                float r = 0.9 + 1.3 * hash21(id + 3.7);
                float tw = 0.55 + 0.45 * sin(uTime * (0.6 + h * 1.4) + h * 40.0);
                float star = smoothstep(r, 0.0, d) * tw * step(0.5, h);
                if (star > a) {
                    a = star;
                    col = mix(uA, uB, step(0.5, hash21(id + 9.9)));
                }
            }
            return half4(half3(col * a), half(a));
        }
    """

    /** The AGSL source for an animated [style], or null when [style] is not animated. */
    fun source(style: String): String? =
        when (style) {
            BackgroundStyles.Aurora -> AURORA
            BackgroundStyles.Mesh -> MESH
            BackgroundStyles.Starfield -> STARFIELD
            else -> null
        }
}

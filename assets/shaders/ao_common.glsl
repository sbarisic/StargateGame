#include "gbuffer_common.glsl"
uniform sampler2D uDepth, uNormals;
uniform mat4 uInverseViewProjection;
uniform vec3 uEye;
vec3 surfacePosition(vec2 uv, float depth) {
    vec4 p = uInverseViewProjection * vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1);
    return p.xyz / p.w;
}
float materialClass(vec4 normal) { return aoClass(floor(normal.a+.5)); }
float receiverWeight(float kind) { return kind < .5 ? 0. : kind < 1.5 ? 1. : kind < 2.5 ? .25 : kind < 3.5 ? .35 : 1.; }
float occluderWeight(float kind) { return kind < .5 ? 0. : kind < 1.5 ? 1. : kind < 2.5 ? .25 : kind < 3.5 ? .15 : 1.; }

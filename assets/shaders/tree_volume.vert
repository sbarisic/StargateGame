#version 400 core
layout(location=0) in vec3 aPosition;
uniform vec4 uScreenBounds;
uniform float uProxyDepth;
out vec2 vUv;
void main() {
    vec2 ndc = mix(uScreenBounds.xy, uScreenBounds.zw, aPosition.xy * .5 + .5);
    gl_Position = vec4(ndc, uProxyDepth, 1);
    vUv = ndc * .5 + .5;
}

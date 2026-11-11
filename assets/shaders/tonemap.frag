#version 400 core
in vec2 vUv;out vec4 fragColor;uniform sampler2D uTexture;
uniform sampler2D uHorizonBloom;uniform float uBloomStrength;
uniform sampler2D uExposure;
#include "agx.glsl"
vec3 encode(vec3 c){return mix(c*12.92,1.055*pow(c,vec3(1.0/2.4))-.055,step(vec3(.0031308),c));}
void main(){vec3 x=max(texture(uTexture,vUv).rgb+texture(uHorizonBloom,vUv).rgb*uBloomStrength,vec3(0));float ev=texelFetch(uExposure,ivec2(0),0).r;fragColor=vec4(encode(agxToneMap(x*exp2(ev))),1);}

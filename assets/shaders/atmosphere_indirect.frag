#version 400 core
#include "atmosphere_parameters.glsl"
uniform sampler2D uTrans,uRay,uMie,uMultiple,uIrr,uDensity;
uniform int uOrder;
vec3 atlasCoord(){return vec3(gl_FragCoord.x,mod(gl_FragCoord.y,64.0),floor(gl_FragCoord.y/64.0)+.5);}
layout(location=0) out vec4 result;void main(){result=vec4(ComputeIndirectIrradianceTexture(ATMOSPHERE,uRay,uMie,uMultiple,gl_FragCoord.xy,uOrder-1),0);}
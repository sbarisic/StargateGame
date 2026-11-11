#version 400 core
#include "atmosphere_parameters.glsl"
uniform sampler2D uTrans,uRay,uMie,uMultiple,uIrr,uDensity;
uniform int uOrder;
vec3 atlasCoord(){return vec3(gl_FragCoord.x,mod(gl_FragCoord.y,64.0),floor(gl_FragCoord.y/64.0)+.5);}
layout(location=0) out vec4 delta;layout(location=1) out vec4 reduced;void main(){float nu;vec3 s=ComputeMultipleScatteringTexture(ATMOSPHERE,uTrans,uDensity,atlasCoord(),nu);delta=vec4(s,0);reduced=vec4(s/RayleighPhaseFunction(nu),0);}
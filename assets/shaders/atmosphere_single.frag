#version 400 core
#include "atmosphere_parameters.glsl"
uniform sampler2D uTrans,uRay,uMie,uMultiple,uIrr,uDensity;
uniform int uOrder;
vec3 atlasCoord(){return vec3(gl_FragCoord.x,mod(gl_FragCoord.y,64.0),floor(gl_FragCoord.y/64.0)+.5);}
layout(location=0) out vec4 ray;layout(location=1) out vec4 mie;void main(){vec3 a,b;ComputeSingleScatteringTexture(ATMOSPHERE,uTrans,atlasCoord(),a,b);ray=vec4(a,0);mie=vec4(b,0);}
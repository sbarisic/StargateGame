#version 400 core
uniform vec3 uPoint;
out vec4 fragColor;
#include "cloud_density.glsl"
#include "blowing_snow.glsl"
void main(){fragColor=vec4(blowingSnow(uPoint)*4.0,0,0,1);}

#version 400 core
uniform sampler2D uShelter;uniform mat4 uShelterMatrix,uShelterInverse;uniform vec3 uPoint;
out vec4 fragColor;
#include "precipitation.glsl"
void main(){vec4 clip=uShelterMatrix*vec4(uPoint,1);float roof=precipitationHeight(clip.xy/clip.w*.5+.5);fragColor=vec4(roof>uPoint.y?1.0:0.0,0,0,1);}

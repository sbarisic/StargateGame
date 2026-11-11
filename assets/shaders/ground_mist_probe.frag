#version 400 core
uniform vec3 uPoint,uEye;
uniform float uRelativeHeight;
out vec4 fragColor;
#include "cloud_density.glsl"
#include "blowing_snow.glsl"
#include "ground_mist.glsl"
#include "mist_rooms.glsl"
void main(){
 vec3 p=uPoint;
 if(uRelativeHeight>=0.0){float surface;if(!mistSurface(p,surface)){fragColor=vec4(0);return;}p.y=surface+uRelativeHeight;}
 float d=groundMist(p,uEye),outside=outsideMist(p);
 fragColor=vec4(d*4.0,d*outside*4.0,outside,1);
}

#version 400 core
in vec2 vUv;out vec4 result;uniform vec3 uSunDirection;uniform float uGroundLevel;
#include "cloud_density.glsl"
void main(){float optical=0;vec3 p=vec3((vUv.x-.5)*4096,uGroundLevel,(vUv.y-.5)*4096);
 if(uSunDirection.y>.01){float a=(uCloudBase-p.y)/uSunDirection.y,b=(uCloudTop-p.y)/uSunDirection.y,dt=(b-a)/16;
 for(int i=0;i<16;i++)optical+=cloudDensity(p+uSunDirection*(a+(float(i)+.5)*dt))*dt*.004;}
 result=vec4(vec3(exp(-optical)),1);}

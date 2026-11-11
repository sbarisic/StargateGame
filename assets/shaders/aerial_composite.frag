#version 400 core
in vec2 vUv;out vec4 result;uniform sampler2D uOpaqueColor,uOpaqueDepth;uniform mat4 uInverseViewProjection;uniform vec3 uViewPosition;uniform float uUnderwater,uWaterHeight;
#include "daylight.glsl"
#include "water_optics.glsl"
uniform int uRealisticWater;
void main(){float depth=texture(uOpaqueDepth,vUv).r;vec3 color=texture(uOpaqueColor,vUv).rgb;vec4 p=uInverseViewProjection*vec4(vUv*2-1,depth*2-1,1);vec3 point=p.xyz/p.w;
 vec3 unfogged=color;
 if(depth<.99999)color=applyFog(color,point,uViewPosition);
 if(uUnderwater>0){vec3 ray=normalize(point-uViewPosition);float distance=min(length(point-uViewPosition),ray.y>.0001?max(0,(uWaterHeight-uViewPosition.y)/ray.y):80.0);
  if(uRealisticWater==1){
   // Air extinction only beyond the exit; do not fog submerged geometry twice.
   vec3 exitPoint=uViewPosition+ray*distance;
   vec3 source=point.y>uWaterHeight&&depth<.99999?waterAirTransport(unfogged,point,exitPoint):unfogged;
   color=mix(color,waterTransport(source,distance,vec3(uViewPosition.x,uWaterHeight,uViewPosition.z)),uUnderwater);
  }else{vec3 t=exp(-vec3(.22,.075,.035)*distance);color=mix(color,color*t+vec3(.018,.065,.08)*(1-t),uUnderwater);}}
 result=vec4(color,1);}

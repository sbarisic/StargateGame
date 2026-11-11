#version 400 core
in vec2 vUv;in vec3 vWorld;in float vAlpha;out vec4 fragColor;
uniform sampler2D uSceneDepth,uShelter;uniform mat4 uInverseViewProjection,uShelterMatrix,uShelterInverse;
uniform vec2 uTargetSize;uniform vec3 uViewPosition;
#include "daylight.glsl"
#include "gate_lighting.glsl"
#include "precipitation.glsl"
void main(){
 float shape=1.0-smoothstep(.1,1.0,dot(vUv,vUv));
 float alpha=vAlpha*shape;if(alpha<.003)discard;
 vec2 uv=gl_FragCoord.xy/uTargetSize;float depth=texture(uSceneDepth,uv).r;
 vec4 far=uInverseViewProjection*vec4(uv*2.0-1.0,depth*2.0-1.0,1);vec3 scene=far.xyz/far.w;
 alpha*=clamp((distance(scene,uViewPosition)-distance(vWorld,uViewPosition))/.25,0.0,1.0);
 vec4 shelter=uShelterMatrix*vec4(vWorld,1);
 float roof=precipitationHeight(shelter.xy/shelter.w*.5+.5);
 alpha*=smoothstep(roof+.015,roof+.12,vWorld.y);
 vec3 base=vec3(.82,.88,.94);vec3 color=base*(ambientSkyColor()*2.5+sunLightColor()*.35);
 color+=gateLighting(vWorld,vec3(0,1,0),normalize(uViewPosition-vWorld),base,.95);
 // Particles add no fog colour of their own: extinction fades them into existing fog.
 alpha*=fogTransmission(vWorld,uViewPosition);
 fragColor=vec4(color,alpha);
}

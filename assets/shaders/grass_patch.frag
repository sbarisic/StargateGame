#version 400 core
uniform vec3 uViewPosition;uniform mat4 uView;uniform sampler2D uTexture;
uniform sampler3D uGrassAmbient;
flat in vec2 vPatch;
in vec3 vWorld,vNormal,vRest;in vec2 vUv;in float vStem;
layout(location=0) out vec4 fragColor;
#include "ao_outputs.glsl"
#include "daylight.glsl"
#include "shadows.glsl"
#include "gate_lighting.glsl"
#include "grass_hybrid.glsl"
void main(){
 if(any(lessThan(vRest.xz,vPatch))||any(greaterThan(vRest.xz,vPatch+2.)))discard;
 if(!grassKept(vRest.xz)||grassHash(vRest.xz)<grassTransition(vRest.xz))discard;
 vec3 n=normalize(vNormal),ambient;
 vec2 baked=transpose(grassRotation(vPatch))*(vRest.xz-vPatch-1.)+1.;
 vec3 local=vec3(baked.x*.5,vRest.y/uGrassExtent,baked.y*.5);
 vec3 color=decodeColor(texture(uTexture,vUv).rgb);
 vec3 light=grassLight(vWorld,n,color,vStem,texture(uGrassAmbient,local).rg,ambient);
 writeAmbient(ambient,gl_FrontFacing?n:-n,3.);
 fragColor=vec4(applyFog(light,vWorld,uViewPosition),1.);
}

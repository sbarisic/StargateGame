#version 400 core
in vec2 vUv;out vec4 result;
#include "gbuffer_common.glsl"
#include "deferred_lighting.glsl"
uniform sampler2D uGAlbedo,uGShading,uGGeometry,uGEmission,uGBaseDepth,uGDepth;
uniform sampler2D uGuideDepth,uGuideNormal;
#include "deferred_ao.glsl"
void main(){vec4 g=texture(uGGeometry,vUv);float d=texture(uGDepth,vUv).r;
 if(g.a<.5||d>=.99999){vec3 ray=normalize(reconstruct(vUv,1.)-uViewPosition);result=uWeapon!=0||uDhdReceiver>0||uPointOnly!=0?vec4(0):vec4(uInspection==0||uInspection==13?deferredSky(ray):(uInspection==1||uInspection==5||uInspection==6?vec3(1):vec3(0)),1);return;}
 result=vec4(deferredLight(vUv,texture(uGAlbedo,vUv),texture(uGShading,vUv),g,texture(uGEmission,vUv),texture(uGBaseDepth,vUv).r,d,deferredAo(vUv,d,g)),uDhdReceiver>0||uPointOnly!=0?0.:1.);
}

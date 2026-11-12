#version 400 core
in vec2 vUv;out vec4 result;
#include "gbuffer_common.glsl"
#include "deferred_lighting.glsl"
uniform sampler2DMS uGAlbedo,uGShading,uGGeometry,uGEmission,uGBaseDepth,uGDepth;
uniform sampler2D uGuideDepth,uGuideNormal;
uniform vec2 uSamplePositions[4];
#include "deferred_ao.glsl"
void main(){ivec2 pixel=ivec2(gl_FragCoord.xy);vec3 sum=vec3(0);float coverage=0.;
 for(int i=0;i<4;i++){
  vec4 g=texelFetch(uGGeometry,pixel,i);float d=texelFetch(uGDepth,pixel,i).r;
  if(g.a<.5||d>=.99999)continue;
  vec2 sampleUv=(vec2(pixel)+uSamplePositions[i])/vec2(textureSize(uGDepth));
  float ao=deferredAo(sampleUv,d,g);
  sum+=deferredLight(sampleUv,texelFetch(uGAlbedo,pixel,i),texelFetch(uGShading,pixel,i),g,texelFetch(uGEmission,pixel,i),texelFetch(uGBaseDepth,pixel,i).r,d,ao);coverage+=1.;
 }
 result=vec4(sum*.25,uDhdReceiver>0||uPointOnly!=0?0.:coverage*.25);
}

#version 400 core
// Glass uses world.vert in the single-sample transparent composition pass.
in vec3 vWorld,vNormal;
in vec2 vUv;
out vec4 result;
uniform sampler2D uTexture,uModelNormal,uMetallicRoughness;
uniform int uHasModelNormal,uHasMetallicRoughness;
uniform vec2 uTargetSize;
uniform vec4 uTravelPlane;
#include "gbuffer_common.glsl"
#include "deferred_lighting.glsl"
void main(){
 if(dot(vec4(vWorld,1),uTravelPlane)<0.)discard;
 vec4 tex=texture(uTexture,vUv);if(tex.a<=.001)discard;
 vec3 gn=normalize(vNormal)*(gl_FrontFacing?1.:-1.),n=gn;
 if(uHasModelNormal!=0){
  vec3 dp1=dFdx(vWorld),dp2=dFdy(vWorld);vec2 duv1=dFdx(vUv),duv2=dFdy(vUv);
  vec3 p2=cross(dp2,gn),p1=cross(gn,dp1);
  vec3 t=p2*duv1.x+p1*duv2.x,b=p2*duv1.y+p1*duv2.y;
  float scale=inversesqrt(max(max(dot(t,t),dot(b,b)),1e-8));
  vec3 detail=texture(uModelNormal,vUv).xyz*2.-1.;detail.y=-detail.y;
  n=normalize(mat3(t*scale,b*scale,gn)*detail);
 }
 vec3 base=decodeColor(tex.rgb);vec4 mr=uHasMetallicRoughness!=0?texture(uMetallicRoughness,vUv):vec4(1,.2,0,0);
 vec2 uv=gl_FragCoord.xy/uTargetSize;
 vec3 lit=deferredLight(uv,vec4(base,mr.g),vec4(octEncode(n),mr.b,1),vec4(octEncode(gn),0,1),vec4(0,0,0,1),gl_FragCoord.z,gl_FragCoord.z,1.);
 vec3 spec,trans;vec3 local=propLightingParts(vWorld,vWorld,gn,n,normalize(uViewPosition-vWorld),base,mr.g,1.,mr.b,0.,spec,trans);lit+=local+spec;
 result=vec4(applyFog(lit,vWorld,uViewPosition),tex.a);
}

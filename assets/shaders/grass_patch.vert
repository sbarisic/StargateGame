#version 400 core
layout(location=0) in vec3 aPosition;
layout(location=3) in vec3 aNormal;
layout(location=2) in vec4 aUv;
uniform vec3 uViewPosition;
// Vertex shader needs only terrain and root-anchored deformation.
uniform float uGrassExtent,uGrassTip;
uniform sampler2D uSurface;uniform vec4 uTile;uniform mat4 uGrassViewProjection;
#include "meadow.glsl"
#include "grass_layout.glsl"
flat out vec2 vPatch;
flat out vec2 vBladeRoot;
out vec3 vWorld,vNormal,vRest;out vec2 vUv;out float vStem;
void main(){
 vec2 origin=uTile.xy+vec2(gl_InstanceID%4,gl_InstanceID/4)*2.;
 mat2 rotation=grassRotation(origin);
 vBladeRoot=origin+1.+rotation*(aUv.zw-1.);
 vPatch=origin;vec2 root=origin+1.+rotation*(aPosition.xz-1.);
 vec4 surface=texture(uSurface,(clamp((root-uTile.xy)/8.,0.,1.)*32.+.5)/33.);
 float height=surface.x,scale=max(.1,surface.z),h=aPosition.y*scale;
 vRest=vec3(root.x,aPosition.y,root.y);
 vWorld=vec3(root.x,height+h,root.y);
 vWorld.xz+=uMeadowWind*meadowSway(root)*.24*h*h/(uGrassTip*scale);
 vec2 derivative=uMeadowWind*meadowSway(root)*.48*h/(uGrassTip*scale);
 vec2 nxz=rotation*aNormal.xz;
 vNormal=normalize(vec3(nxz.x,aNormal.y/scale-dot(derivative,nxz),nxz.y));
 vStem=aPosition.y/uGrassTip;vUv=aUv.xy;
 gl_Position=uGrassViewProjection*vec4(vWorld,1.);
}

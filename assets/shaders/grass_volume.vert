#version 400 core
layout(location=0) in vec3 aPosition;
uniform vec4 uTile;uniform mat4 uGrassViewProjection;
flat out vec2 vPatch;
flat out float vGrassMip;
uniform float uGrassPatchData[64];
uniform int uGrassDepthCulling,uGrassConservativeDepth;
void main(){
 vPatch=uTile.xy+vec2(gl_InstanceID%4,gl_InstanceID/4)*2.;
 int data=gl_InstanceID*4; vGrassMip=uGrassPatchData[data+3];
 float lowY=uGrassDepthCulling!=0?uGrassPatchData[data]:uTile.z;
 float highY=uGrassDepthCulling!=0?uGrassPatchData[data+1]:uTile.w;
 vec2 lo=vec2(1),hi=vec2(-1);bool behind=false;
 for(int i=0;i<8;i++){
  vec3 p=vec3(vPatch.x+((i&1)==0?-.3:2.3),(i&2)==0?lowY:highY,vPatch.y+((i&4)==0?-.3:2.3));
  vec4 c=uGrassViewProjection*vec4(p,1);if(c.w<=.01)behind=true;else {lo=min(lo,c.xy/c.w);hi=max(hi,c.xy/c.w);}
 }
 if(behind){lo=vec2(-1);hi=vec2(1);}
 gl_Position=vec4(mix(clamp(lo,-1.,1.),clamp(hi,-1.,1.),aPosition.xy*.5+.5),uGrassConservativeDepth!=0?uGrassPatchData[data+2]:-1.,1);
}

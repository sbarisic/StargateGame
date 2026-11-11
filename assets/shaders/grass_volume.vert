#version 400 core
layout(location=0) in vec3 aPosition;
uniform vec4 uTile;uniform mat4 uGrassViewProjection;
flat out vec2 vPatch;
void main(){
 vPatch=uTile.xy+vec2(gl_InstanceID%4,gl_InstanceID/4)*2.;
 vec2 lo=vec2(1),hi=vec2(-1);bool behind=false;
 for(int i=0;i<8;i++){
  vec3 p=vec3(vPatch.x+((i&1)==0?-.3:2.3),(i&2)==0?uTile.z:uTile.w,vPatch.y+((i&4)==0?-.3:2.3));
  vec4 c=uGrassViewProjection*vec4(p,1);if(c.w<=.01)behind=true;else {lo=min(lo,c.xy/c.w);hi=max(hi,c.xy/c.w);}
 }
 if(behind){lo=vec2(-1);hi=vec2(1);}
 gl_Position=vec4(mix(clamp(lo,-1.,1.),clamp(hi,-1.,1.),aPosition.xy*.5+.5),0,1);
}

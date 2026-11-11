#version 400 core
uniform vec3 uViewPosition;uniform mat4 uView;
uniform sampler3D uGrassColor,uGrassNormal,uGrassAmbient;
uniform sampler2D uSceneDepth;
flat in vec2 vPatch;
layout(location=0) out vec4 fragColor;
#include "ao_outputs.glsl"
#include "daylight.glsl"
#include "shadows.glsl"
#include "gate_lighting.glsl"
#include "grass_hybrid.glsl"
vec3 grassUv(vec3 p){
 float h=p.y-grassSurface(p.xz).x;
 vec2 rest=p.xz-grassBend(p.xz,h);
 h=p.y-grassSurface(rest).x;
 vec2 baked=transpose(grassRotation(vPatch))*(rest-vPatch-1.)+1.;
 return vec3(baked.x*.5,h/(uGrassExtent*max(.1,grassSurface(rest).z)),baked.y*.5);
}
float extinction(vec3 uv,float mip){
 if(any(lessThan(uv,vec3(0)))||any(greaterThan(uv,vec3(1))))return 0.;
 // The bake wraps crossing blades. Do not clear a border around each patch:
 // that turns the two-metre acceleration grid into visible rectangular gaps.
 return -log(max(1.-textureLod(uGrassNormal,uv,mip).a,.001))/.015625;
}
void main(){
 vec2 screen=gl_FragCoord.xy/uViewport;
 vec4 farPoint=uInverseViewProjection*vec4(screen*2.-1.,1,1);vec3 ray=normalize(farPoint.xyz/farPoint.w-uViewPosition);
 vec3 minimum=vec3(vPatch.x-.3,uTile.z,vPatch.y-.3),maximum=vec3(vPatch.x+2.3,uTile.w,vPatch.y+2.3);
 float begin=0.,end=1e8;
 for(int axis=0;axis<3;axis++){
  if(abs(ray[axis])<.000001){if(uViewPosition[axis]<minimum[axis]||uViewPosition[axis]>maximum[axis])discard;}
  else{float a=(minimum[axis]-uViewPosition[axis])/ray[axis],b=(maximum[axis]-uViewPosition[axis])/ray[axis];begin=max(begin,min(a,b));end=min(end,max(a,b));}
 }
 float depth=texture(uSceneDepth,screen).r;
 // Depth is OpenGL window depth, independent of the short/meadow volume height.
 if(depth<1.){vec4 p=uInverseViewProjection*vec4(screen*2.-1.,depth*2.-1.,1);end=min(end,dot(p.xyz/p.w-uViewPosition,ray));}
 if(end<=begin)discard;
 float distanceToPatch=distance(vPatch+1.,uViewPosition.xz);
 if(distanceToPatch+2.<uTransition.x||distanceToPatch-2.>uRange)discard;
 float mip=clamp(log2(max(1.,distanceToPatch/32.)),0.,2.);
 float stepSize=max(.010*exp2(mip),(end-begin)/191.);
 float optical=0.;vec3 hit=vec3(0),uv=vec3(0);bool found=false;
 for(int i=0;i<192;i++){
  float t=begin+(float(i)+.5)*stepSize;if(t>=end)break;
  vec3 p=uViewPosition+ray*t;uv=grassUv(p);
  if(any(lessThan(uv,vec3(0)))||any(greaterThan(uv,vec3(1))))continue;
  // Conservative max-occupancy mips only skip proven-empty cells.
  if(textureLod(uGrassColor,uv,2.).a<.001)continue;
  vec2 rest=vPatch+1.+grassRotation(vPatch)*(uv.xz*2.-1.);
  if(!grassKept(rest)||grassHash(rest)>=grassTransition(rest))continue;
  float e=extinction(uv,mip);optical+=e*stepSize;
  if(optical>.38){hit=p;found=true;break;}
 }
 if(!found)discard;
 vec3 normal=normalize(textureLod(uGrassNormal,uv,mip).xyz*2.-1.);
 float scale=max(.1,grassSurface(hit.xz).z),h=uv.y*uGrassExtent*scale;
 vec2 derivative=uMeadowWind*meadowSway(hit.xz)*.48*h/(uGrassTip*scale);
 vec2 nxz=grassRotation(vPatch)*normal.xz;
 normal=normalize(vec3(nxz.x,normal.y/scale-dot(derivative,nxz),nxz.y));
 float sunDepth=0.;for(int i=0;i<4;i++)sunDepth+=extinction(grassUv(hit+uSunDirection*(.025+float(i)*.065)),mip)*.065;
 vec3 ambient;vec3 albedo=decodeColor(textureLod(uGrassColor,uv,mip).rgb);
 vec3 light=grassLight(hit,normal,albedo,h/(uGrassTip*scale),texture(uGrassAmbient,uv).rg,ambient);
 light=ambient+(light-ambient)*mix(.28,1.,exp(-sunDepth));
 vec4 clip=uGrassViewProjection*vec4(hit,1.);gl_FragDepth=clip.z/clip.w*.5+.5;
 writeAmbient(ambient,dot(normal,-ray)<0.?-normal:normal,3.);
 fragColor=vec4(applyFog(light,hit,uViewPosition),1.);
}

#version 400 core
out vec4 fragColor;uniform int uMode;uniform vec3 uPoint;uniform vec3 uViewPosition;
#include "daylight.glsl"
#include "meadow.glsl"
#include "terrain_pbr.glsl"
void main(){
 if(uMode==0){
  TerrainSurface s=terrainMaterial(uPoint,normalize(vec3(.2,1,.1)),vec4(.8,.2,.1,0),5);
  int failure=0;
  if(any(isnan(s.color))||any(isinf(s.color)))failure|=1;
  if(any(isnan(s.normal))||abs(length(s.normal)-1)>=.001)failure|=2;
  if(isnan(s.rough)||s.rough<.14999||s.rough>1.00001)failure|=4;
  if(isnan(s.depth)||isinf(s.depth)||s.depth<-.00001)failure|=8;
  fragColor=vec4(failure==0?1:0,texture(uTerrainMasks,vec3(.37,.61,0)).a,float(failure)/255.0,1);
 }else fragColor=vec4(clamp(dustDensity(uPoint)*50,0,1),outsideMist(uPoint),looseSand(uPoint.xz,surfaceData(uPoint.xz)),1);
}

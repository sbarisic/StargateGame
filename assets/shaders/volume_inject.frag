#version 400 core
out vec4 result;uniform mat4 uInverseViewProjection,uView;uniform vec3 uViewPosition;uniform vec3 uGrid;
#include "daylight.glsl"
#include "shadows.glsl"
#include "gate_lighting.glsl"
#include "cloud_density.glsl"
#include "kawoosh_mist.glsl"
#include "blowing_snow.glsl"
#include "ground_mist.glsl"
#include "mist_rooms.glsl"
float distanceAt(float slice){return pow(6501.0,slice/uGrid.z)-1.0;}
void main(){
 vec2 cell=mod(gl_FragCoord.xy,uGrid.xy);float layer=floor(gl_FragCoord.x/uGrid.x)+floor(gl_FragCoord.y/uGrid.y)*8.0;
 if(layer>=uGrid.z){result=vec4(0);return;}
 vec2 uv=cell/uGrid.xy;vec4 farP=uInverseViewProjection*vec4(uv*2-1,1,1);vec3 ray=normalize(farP.xyz/farP.w-uViewPosition);
 float distance=(distanceAt(layer)+distanceAt(layer+1))*.5;vec3 p=uViewPosition+ray*distance;
 float low=exp(-max(p.y-uGroundLevel,0)*uFog.z);
 float density=uFog.x+outsideMist(p)*uFog.y*low*(.7+.6*cloudNoise(p*.018));
 // Average across the depth cell: shallow ground wisps must not turn into
 // alternating slices as the logarithmic froxel spacing grows with distance.
 float stepLength=distanceAt(layer+1)-distanceAt(layer);
 float drift=(blowingSnow(p-ray*stepLength*.3333)+blowingSnow(p)+blowingSnow(p+ray*stepLength*.3333))/3.0;
 density+=outsideMist(p)*drift;
 float mist=0.0;
 for(int i=0;i<5;i++){
  vec3 samplePoint=p+ray*stepLength*(float(i)*.2-.4);
  float localMist=groundMist(samplePoint,uViewPosition)+woodlandMist(samplePoint);
  if(localMist>0.0)mist+=localMist*outsideMist(samplePoint);
 }
 density+=mist*.2;
 vec3 ambient=ambientSkyColor();float visibility=SampleSunVisibility(p,vec3(0,1,0),max(uSunDirection.y,0))*cloudVisibility(p);
 float mu=dot(ray,uSunDirection),phase=(1-.49)/(pow(1+.49-1.4*mu,1.5));
 vec3 light=ambient*.7+sunLightColor()*max(0.0,uSunDirection.y)*visibility*phase*.25;
 for(int i=0;i<uGateLightCount;i++){
  if(dot(uGateColorRadius[i].rgb,uGateColorRadius[i].rgb)==0.0)continue;
  vec3 d=uGatePositionRange[i].xyz-p;float r=length(d),range=uGatePositionRange[i].w;if(r>=range||r<.001)continue;
  float a=pow(max(0.0,1-pow(r/range,4)),2)/(r*r+uGateColorRadius[i].w*uGateColorRadius[i].w);
  light+=uGateColorRadius[i].rgb*a*gateVisibility(i,p,vec3(0),d/r)*.08;
 }
 // Clear air and local aerosol share one transport integral. Coefficients are per metre.
 float altitude=max(0,p.y-uGroundLevel);vec3 rayleigh=vec3(5.802,13.558,33.100)*1e-6*exp(-altitude/8000.0);
 float mie=3.996e-6*exp(-altitude/1200.0),rayPhase=.0596831*(1+mu*mu);
 vec3 air=(rayleigh*rayPhase+vec3(mie*phase*.0795775))*sunLightColor()*visibility+rayleigh*ambient;
 float extinction=dot(rayleigh,vec3(.2126,.7152,.0722))+mie*1.11;
 float dust=(dustDensity(p-ray*stepLength*.3333)+dustDensity(p)+dustDensity(p+ray*stepLength*.3333))/3.0;
 float gateMist=0.0;
 if(uKawooshCount>0){
  for(int i=0;i<uKawooshSamples;i++)
   gateMist+=kawooshMist(p+ray*stepLength*((float(i)+.5)/float(uKawooshSamples)-.5));
  gateMist/=float(uKawooshSamples);
 }
 // Existing shadowed sun/gate illumination, plus light scattered from the
 // luminous water itself. The density envelope also gates this contribution.
 vec3 sprayLight=light*vec3(.82,.92,1.0)+vec3(.20,.36,.55);
 result=vec4(light*density+air+dustLight()*dust+sprayLight*gateMist,density+extinction+dust+gateMist);
}

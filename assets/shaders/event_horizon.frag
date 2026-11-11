#version 400 core
in vec3 vWorld,vGatePosition;
in vec2 vMembrane;
in vec3 vSurfaceNormal,vSurfaceTangent,vSurfaceBitangent;
layout(location=0) out vec4 fragColor;
#include "ao_outputs.glsl"

uniform vec3 uViewPosition,uGateCentre,uBlue;
uniform float uRadius,uTime,uWaveScale,uDistortion,uSpeed,uNormalStrength,uHighlightWidth,uCentreEmphasis;
uniform float uGrazingCompensation,uEmissionIntensity;
uniform sampler2D uWaveState,uWaveObstacles;
uniform int uHasWaveState;
uniform float uWakeFade;
uniform float uWaveCell,uDisturbanceSmoothing,uRimBrightness,uContactRamp;
uniform vec3 uGateNormal,uGateRight,uGateUp;
uniform int uEmissiveOnly;
uniform float uKawooshBloom;

uniform sampler2D uSceneDepth;
uniform vec2 uMaskSize;
uniform vec2 uEdgeFade;
#include "daylight.glsl"
#include "horizon_baseline.glsl"
#include "gate_transition.glsl"
// Cubic B-spline reconstruction keeps the warp's derivatives continuous across
// simulation cells. Bilinear height gradients caused the blocky highlights.
vec4 smoothWave(sampler2D field,vec2 uv){
 vec2 size=vec2(textureSize(field,0)),p=uv*size-.5,f=fract(p);p=floor(p);
 vec2 w0=(1.0-f)*(1.0-f)*(1.0-f)/6.0;
 vec2 w1=(3.0*f*f*f-6.0*f*f+4.0)/6.0;
 vec2 w2=(-3.0*f*f*f+3.0*f*f+3.0*f+1.0)/6.0;
 vec2 w3=f*f*f/6.0;
 vec2 a=w0+w1,b=w2+w3;
 vec2 lo=(p-.5+w1/a)/size,hi=(p+1.5+w3/b)/size;
 return texture(field,lo)*a.x*a.y+texture(field,vec2(hi.x,lo.y))*b.x*a.y
       +texture(field,vec2(lo.x,hi.y))*a.x*b.y+texture(field,hi)*b.x*b.y;
}
vec2 contactWarp(vec2 p){
 vec2 uv=p/(2.0*uRadius)+.5;
 float span=max(.05,uWaveCell);vec2 d=vec2(span/(2.0*uRadius),0);
 vec4 mask=smoothWave(uWaveObstacles,uv);
 vec2 gradient=vec2(smoothWave(uWaveState,uv+d).r-smoothWave(uWaveState,uv-d).r,
                    smoothWave(uWaveState,uv+d.yx).r-smoothWave(uWaveState,uv-d.yx).r)/(2.0*span);
 vec2 contour=vec2(smoothWave(uWaveObstacles,uv+d).a-smoothWave(uWaveObstacles,uv-d).a,
                   smoothWave(uWaveObstacles,uv+d.yx).a-smoothWave(uWaveObstacles,uv-d.yx).a)/(2.0*span);
 // Bend the existing pattern; do not add a second height-wave highlight.
 vec2 warp=-gradient*.65+contour*.014*(1.0-mask.g)*uContactRamp;
 warp*=1.0-smoothstep(uRadius-.15,uRadius,length(p));
 return uWakeFade*warp*.12/sqrt(.12*.12+dot(warp,warp));
}
vec3 reflectionEnvironment(vec3 r,float width,float detailFade){
 vec2 q=r.xy/max(abs(r.z),.35);
 // Broad blue studio environment, with white bands reflected through the wave normals.
 float band1=exp(-pow((q.y+.08+.12*sin(q.x*2.7))/width,2.0)-pow((q.x+.06)/.30,2.0));
 float band2=exp(-pow((q.y-.44+.08*sin(q.x*4.1+1.0))/(width*.8),2.0)-pow((q.x-.26)/.23,2.0));
 float band3=exp(-pow((q.y+.36-.06*sin(q.x*3.0))/(width*1.3),2.0)-pow((q.x+.3)/.25,2.0));
 float softbox=exp(-dot(q-vec2(.05,-.1),q-vec2(.05,-.1))*8.0);
 float lights=band1+band2*.65+band3*.4;
 return vec3(.004,.009,.022)+vec3(.045,.075,.13)*softbox*mix(.3,1.0,detailFade)+vec3(1.05,1.35,1.8)*lights*detailFade;
}
void main(){excludeAmbient();
 if(uBackSheet!=0&&dot(uViewPosition-uGateCentre,uGateNormal)>=0.0)discard;
 float radial=clamp(length(vMembrane)/uRadius,0.0,1.0);
 float activity=surgeActivity();
 float phaseAge=max(uOpeningAge,uClosingAge);
 vec3 fluid=vec3(0),fineFluid=vec3(0),plumeGradient=vec3(0);
 if(activity>0.0){
  // Metre-space projections follow the extruded surface, so detail cannot
  // stretch into longitudinal stripes as the disc becomes a long plume.
  vec3 q=vGatePosition+vec3(.13,-.22,.35)*phaseAge;
  float pixel=max(length(dFdx(q)),length(dFdy(q)));
  q+=.10*vec3(sin(q.y*2.3+q.z),sin(q.z*2.1+q.x),sin(q.x*2.7+q.y));
  vec4 coarse=plumeNoise(q*6.3),fine=plumeNoise(q*21.7+vec3(13.2,7.1,19.7));
  float fineFade=1.0-smoothstep(.25,.8,pixel*21.7);
  fluid=vec3(coarse.x,0,0);fineFluid=vec3(fine.x*fineFade,0,0);
  vec3 g=coarse.yzw*.45+fine.yzw*fineFade*.65;
  plumeGradient=uGateRight*g.x+uGateUp*g.y+uGateNormal*g.z;
 }
 // Formation and removal use fixed gate-space coordinates in every pass.
 float breakupEdge=0.0,remnant=1.0;
 if(uGateSurface.z>0.0||uClosingAge>=0.0){
  vec3 broad=noiseHeightGradient(vMembrane*3.1,.41);
  vec3 fine=noiseHeightGradient(vMembrane*19.0,.92);
  // Static threshold: increasing breakup cannot restore a removed fragment.
  float threshold=clamp(.05+.95*radial+.22*broad.x+.09*fine.x,.015,1.0);
  if(uGateSurface.z>=threshold)discard;
  breakupEdge=1.0-smoothstep(0.0,.075,threshold-uGateSurface.z);
  remnant=uGateSurface.w;
 }
 if(uEmissiveOnly!=0){
  float sceneDepth=texture(uSceneDepth,gl_FragCoord.xy/uMaskSize).r;
  if(gl_FragCoord.z>sceneDepth+0.000002)discard;
 }
 float footprint=max(length(dFdx(vMembrane)),length(dFdy(vMembrane)));
 vec2 samplePosition=vMembrane;
 mat2 jacobian=mat2(1.0);
 if(uHasWaveState!=0){
  vec2 warp=contactWarp(vMembrane);
  // Screen derivatives recover the coordinate-warp Jacobian with one field
  // evaluation, instead of five texture-heavy evaluations per fragment.
  mat2 screenToSurface=mat2(dFdx(vMembrane),dFdy(vMembrane));
  if(abs(determinant(screenToSurface))>1e-12)
   jacobian+=mat2(dFdx(warp),dFdy(warp))*inverse(screenToSurface);
  samplePosition+=warp;
 }
 vec3 base=baseline(samplePosition,uTime*uSpeed,footprint);
 vec2 gradient=transpose(jacobian)*base.yz;

 vec3 view=normalize(uViewPosition-vWorld);
 float side=dot(view,uGateNormal)>=0.0?1.0:-1.0;
 vec3 surfaceNormal=normalize(vSurfaceNormal);
 vec3 n=normalize(surfaceNormal-uNormalStrength*(gradient.x*vSurfaceTangent+gradient.y*vSurfaceBitangent));
 n=normalize(mix(n,normalize(surfaceNormal-plumeGradient+surfaceNormal*dot(plumeGradient,surfaceNormal)),activity));
 if(dot(n,view)<0.0)n=-n;
 vec3 reflectedWorld=reflect(-view,n);
 vec3 reflected=vec3(dot(reflectedWorld,uGateRight),dot(reflectedWorld,uGateUp),dot(reflectedWorld,uGateNormal)*side);
 float centre=exp(-radial*radial*3.5);
 float width=max(uHighlightWidth,footprint*.8);
 // Fade reflected detail, not opacity or wavelength: the rim remains a solid blue membrane.
 float edge=1.0-smoothstep(uEdgeFade.x,uEdgeFade.y,radial);
 // Stabilize the virtual reflection environment at grazing views without stretching waves.
 // A slope-driven lobe retains folds when the physical reflection misses the studio bands.
 float grazing=smoothstep(.12,.8,1.0-abs(dot(view,uGateNormal)))*uGrazingCompensation;
 vec3 slopeReflection=reflect(vec3(0,0,-1),normalize(vec3(-gradient*uNormalStrength,1)));
 vec3 reflectedLight=mix(reflectionEnvironment(reflected,width,mix(.025,1.0,edge*edge)),
     reflectionEnvironment(slopeReflection,width*1.2,mix(.025,1.0,edge*edge)),grazing);
 float rim=mix(1.0,uRimBrightness,smoothstep(.78,1.0,radial));
 vec3 color=(uBlue*(.55+centre*.8)+reflectedLight*(.22+uCentreEmphasis*centre))*rim;
 vec3 emission=reflectedLight*(.22+uCentreEmphasis*centre)*rim*uEmissionIntensity;
 // Aerated silver-blue water: broad reflections wrap geometric folds, with
 // darker cavities. The settled membrane is exactly the existing material.
 float rolling=pow(clamp(.5+.5*dot(n,normalize(uGateRight*(-.4)+uGateUp*.7+uGateNormal*.6)),0.0,1.0),3.0);
 float froth=smoothstep(-.13,.24,fluid.x+fineFluid.x*.50)*(.35+.65*rolling);
 float fresnel=pow(1.0-abs(dot(n,view)),3.0);
 float cavity=mix(1.0,.72+.20*rolling,surgeAmount());
 float glint=pow(max(0.0,dot(reflect(-normalize(uGateRight*(-.4)+uGateUp*.7+uGateNormal*.6),n),view)),14.0);
 vec3 surgeColor=(vec3(.075,.15,.25)+vec3(.38,.51,.68)*(froth*.24+rolling*.35+fresnel*.32)+vec3(.7,.85,1.0)*glint*.60)*cavity;
 color=mix(color,surgeColor,activity*.98);
 emission=mix(emission,surgeColor*.42,activity*.98);
 if(uGateSurface.y>0.0||uClosingAge>=0.0){
  vec3 whiteSheet=vec3(2.5,2.7,3.0)*uGateSurface.y;
  vec3 edges=vec3(1.2,1.5,1.9)*breakupEdge;
  color=(mix(color,whiteSheet,uGateSurface.y)+edges)*remnant;
  emission=(mix(emission,whiteSheet*.7,uGateSurface.y)+edges*.65)*remnant;
 }
 color=applyFog(color+emission,vWorld,uViewPosition);
 if(uEmissiveOnly!=0){
  // Fog attenuates emission; it must never add sky colour to the bloom mask.
  vec3 surgeBloom=max(surgeColor-vec3(.10,.13,.17),vec3(0))*3.8+vec3(.35,.48,.65)*glint;
  color=(emission+surgeBloom*activity*uKawooshBloom)*fogTransmission(vWorld,uViewPosition);
 }
 fragColor=vec4(color,1);
}

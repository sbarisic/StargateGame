#version 400 core
#include "terrain_coverage.glsl"
in vec3 vWorld;in vec2 vUv;in float vWaterDepth;out vec4 fragColor;
uniform sampler2D uOpaqueColor,uOpaqueDepth,uWaterNormals;
uniform vec2 uTargetSize,uWaterWind;
uniform mat4 uInverseViewProjection,uView,uViewProjection;
uniform vec3 uViewPosition,uRippleMetres,uRippleStrength;
uniform float uTime,uUnderwater,uFoamStrength;
uniform int uWaterReflections,uWaterTraceSteps,uWaterRefineSteps,uWaterDiagnostic;
#include "daylight.glsl"
#include "shadows.glsl"
#include "gate_lighting.glsl"
#include "water_optics.glsl"
vec3 reconstruct(vec2 uv,float depth){vec4 p=uInverseViewProjection*vec4(uv*2.0-1.0,depth*2.0-1.0,1.0);return p.xyz/p.w;}
vec4 rippleLayer(vec2 q,vec2 travel,float metres,float strength,mat2 rotation){
 float phase=fract(uTime/8.0),other=fract(phase+.5),weight=abs(phase-.5)*2.0;
 vec2 base=rotation*q/metres,flow=rotation*travel*8.0/metres;
 // Gradients exclude phase wraps. Offline mips average slopes, flattening
 // subpixel ripples instead of normalizing them back into sparkling peaks.
 vec2 dx=dFdx(base),dy=dFdy(base);
 vec4 a=textureGrad(uWaterNormals,base-flow*phase,dx,dy);
 vec4 b=textureGrad(uWaterNormals,base-flow*other+vec2(.37,.61),dx,dy);
 vec4 tex=mix(a,b,weight);
 vec2 slope=transpose(rotation)*(tex.rg*2.0-1.0);
 float footprint=max(length(dx),length(dy));
 return vec4(slope*strength*(1.0-smoothstep(.12,.5,footprint)),0,tex.a);
}
bool tracePoint(vec3 start,vec3 direction,float distance,out vec2 uv,out vec3 scene,out float delta){
 vec3 point=start+direction*distance;vec4 clip=uViewProjection*vec4(point,1.0);
 if(clip.w<=0.0)return false;
 uv=clip.xy/clip.w*.5+.5;
 if(any(lessThanEqual(uv,vec2(.002)))||any(greaterThanEqual(uv,vec2(.998))))return false;
 float depth=textureLod(uOpaqueDepth,uv,0).r;
 if(depth>=.99999){delta=-10000.0;scene=point;return true;}
 scene=reconstruct(uv,depth);delta=-(uView*vec4(point,1.0)).z+(uView*vec4(scene,1.0)).z;return true;
}
vec3 reflectionColor(vec3 direction){
 vec3 tangent=normalize(cross(direction,abs(direction.y)<.95?vec3(0,1,0):vec3(1,0,0)));
 vec3 fallback=(skyColor(direction)*2.0+skyColor(normalize(direction+tangent*uWaterRoughness))+skyColor(normalize(direction-tangent*uWaterRoughness)))*.25;
 float canopy=surfaceData(vWorld.xz).r;
 fallback=mix(fallback,ambientSkyColor()*.35,clamp(canopy*.7,0.0,.7));
 bool below=uUnderwater>.5;
 if(below)fallback=waterScatteringAt(vWorld)*max(.08,length(ambientSkyColor()));
 if(uWaterReflections==0||(below?direction.y>=-.005:direction.y<=.005))return fallback;
 vec3 start=vWorld+vec3(0,below?-.035:.035,0);float previous=.02,previousDelta=-1;
 for(int i=1;i<=24;i++){
  if(i>uWaterTraceSteps)break;
  float fraction=float(i)/float(uWaterTraceSteps),distance=.08+60.0*fraction*fraction;
  vec2 uv;vec3 scene;float delta;
  if(!tracePoint(start,direction,distance,uv,scene,delta))break;
  if(delta>=0.0&&previousDelta<0.0){
   float lo=previous,hi=distance;bool valid=true;
   vec2 hitUv=uv;vec3 hitScene=scene;float hitDelta=delta;
   for(int j=0;j<4;j++){
    if(j>=uWaterRefineSteps)break;
    float mid=(lo+hi)*.5;
    if(!tracePoint(start,direction,mid,uv,scene,delta)){valid=false;break;}
    if(delta>=0.0){hi=mid;hitUv=uv;hitScene=scene;hitDelta=delta;}else lo=mid;
   }
   uv=hitUv;scene=hitScene;delta=hitDelta;
   float tolerance=.12+hi*.008;
   // Reject foreground silhouettes and geometry below this water surface.
   bool correctSide=below?scene.y<vWorld.y-.025:scene.y>vWorld.y+.025;
   if(valid&&delta>=0.0&&delta<tolerance&&correctSide&&dot(scene-start,direction)>0.0){
    float edge=smoothstep(0.0,.10,min(min(uv.x,1.0-uv.x),min(uv.y,1.0-uv.y)));
    float confidence=edge*(1.0-smoothstep(tolerance*.25,tolerance,delta))*(1.0-smoothstep(40.0,60.0,hi));
    // Source is unfogged. Apply extinction along this reflection segment only.
    // Roughness applies to reflected scene geometry too, not only the sky.
    // Reject taps across depth discontinuities to keep shoreline silhouettes clean.
    vec3 hit=textureLod(uOpaqueColor,uv,0).rgb;float weightSum=1.0;
    float radius=clamp(uWaterRoughness*hi*.4,0.0,6.0);
    const vec2 offsets[4]=vec2[4](vec2(1,0),vec2(-1,0),vec2(0,1),vec2(0,-1));
    for(int tap=0;tap<4;tap++){
     vec2 sampleUv=clamp(uv+offsets[tap]*radius/uTargetSize,vec2(.001),vec2(.999));
     float sampleDepth=textureLod(uOpaqueDepth,sampleUv,0).r;
     vec3 sampleWorld=reconstruct(sampleUv,sampleDepth);
     float weight=sampleDepth<.99999?1.0-smoothstep(.15,max(.3,hi*.05),length(sampleWorld-scene)):0.0;
     hit+=textureLod(uOpaqueColor,sampleUv,0).rgb*weight;weightSum+=weight;
    }
    hit/=weightSum;
    hit=below?waterTransport(hit,length(scene-vWorld),vWorld):waterAirTransport(hit,scene,vWorld);
    return mix(fallback,hit,confidence);
   }
   // One refined candidate per pixel keeps the whole trace within 12+3/24+4.
   return fallback;
  }
  previous=distance;previousDelta=delta;
 }
 return fallback;
}
void main(){terrainCoverage(vWorld);
 if(vWaterDepth<.001)discard;
 vec2 uv=gl_FragCoord.xy/uTargetSize;vec3 view=normalize(uViewPosition-vWorld);
 float vertical=max(0.0,vWaterDepth),shore=smoothstep(0.0,.18,vertical);
 vec2 flow=vUv,travel=flow+uWaterWind*.018;
 vec4 broad=rippleLayer(vWorld.xz,travel,uRippleMetres.x,uRippleStrength.x,mat2(1,0,0,1));
 vec4 medium=rippleLayer(vWorld.xz,travel*1.07,uRippleMetres.y,uRippleStrength.y,mat2(.8,.6,-.6,.8));
 vec4 fine=rippleLayer(vWorld.xz,travel*.93,uRippleMetres.z,uRippleStrength.z,mat2(.28,-.96,.96,.28));
 vec2 slope=(broad.xy+medium.xy+fine.xy)*shore;
 vec3 n=normalize(vec3(-slope.x,1,-slope.y));if(dot(n,view)<0.0)n=-n;
 float baseDepth=textureLod(uOpaqueDepth,uv,0).r;vec3 originalBed=reconstruct(uv,baseDepth);
 float originalThickness=baseDepth<.99999?length(originalBed-vWorld):80.0;
 // Pixel-scaled displacement fades at contacts to avoid refraction halos.
 vec2 refractUv=uv+slope*18.0/uTargetSize*smoothstep(0.0,.5,originalThickness);
 bool inside=all(greaterThan(refractUv,vec2(.001)))&&all(lessThan(refractUv,vec2(.999)));
 float refractDepth=textureLod(uOpaqueDepth,clamp(refractUv,vec2(.001),vec2(.999)),0).r;
 vec3 bed=reconstruct(refractUv,refractDepth);
 bool foreground=dot(bed-uViewPosition,vWorld-uViewPosition)<dot(vWorld-uViewPosition,vWorld-uViewPosition);
 bool wrongSide=uUnderwater<.5?bed.y>vWorld.y+.02:bed.y<vWorld.y-.02;
 if(!inside||foreground||wrongSide){refractUv=uv;bed=originalBed;refractDepth=baseDepth;}
 float thickness=refractDepth<.99999?length(bed-vWorld):80.0;
 thickness=min(thickness,vertical/max(abs(view.y),.06));
 vec3 refracted=textureLod(uOpaqueColor,refractUv,0).rgb,transmitted=waterTransport(refracted,thickness,vWorld);
 float nv=max(dot(view,n),.001),fresnel=waterFresnel(nv);
 vec3 reflected=reflectionColor(reflect(-view,n)),color=mix(transmitted,reflected,fresnel*uWaterReflectionStrength);
 vec3 halfVector=(view+uSunDirection)/max(length(view+uSunDirection),.0001);
 float nl=max(dot(n,uSunDirection),0.0),nh=max(dot(n,halfVector),0.0);
 float alpha=uWaterRoughness*uWaterRoughness,a2=alpha*alpha,denominator=nh*nh*(a2-1.0)+1.0;
 float distribution=a2/(3.14159265*denominator*denominator);
 float geometry=2.0*nv/(nv+sqrt(a2+(1.0-a2)*nv*nv));
 geometry*=2.0*nl/max(.0001,nl+sqrt(a2+(1.0-a2)*nl*nl));
 float spec=distribution*geometry*waterFresnel(max(dot(view,halfVector),0.0))/(4.0*nv);
 float visibility=cloudVisibility(vWorld)*SampleSunVisibility(vWorld,vec3(0,1,0),nl);
 color+=sunLightColor()*spec*visibility*uWaterReflectionStrength;
 float current=smoothstep(.08,1.3,length(flow));
 float shoreline=(1.0-smoothstep(.04,.28,vertical))*shore;
 float contact=(1.0-smoothstep(.03,.18,originalThickness))*smoothstep(.005,.04,originalThickness);
 float islands=smoothstep(.63,.79,medium.a*.65+fine.a*.35);
 float foam=clamp((shoreline*(.04+current)+contact*(.15+current))*islands*uFoamStrength,0.0,1.0);
 color=mix(color,ambientSkyColor()*.7+sunLightColor()*.25*visibility,foam);
 color+=gateLighting(vWorld,n,view,vec3(.025,.07,.09),uWaterRoughness);
 color=mix(textureLod(uOpaqueColor,uv,0).rgb,color,smoothstep(0.0,.04,vertical));
 vec3 above=applyFog(color,vWorld,uViewPosition);
 if(uUnderwater>0.0){
  float critical=sqrt(1.0-1.0/(uWaterIor*uWaterIor)),tir=1.0-smoothstep(critical-.015,critical+.015,nv);
  vec3 air=waterAirTransport(refracted,bed,vWorld);
  vec3 underside=mix(air,reflected,max(tir,fresnel));
  above=mix(above,waterTransport(underside,length(uViewPosition-vWorld),vWorld),uUnderwater);
 }
 if(uWaterDiagnostic==1)above=vec3(1.0-exp(-thickness*.25));
 else if(uWaterDiagnostic==2)above=n*.5+.5;
 else if(uWaterDiagnostic==3)above=vec3(flow*.25+.5,0);
 else if(uWaterDiagnostic==4)above=reflected;
 else if(uWaterDiagnostic==5)above=transmitted;
 else if(uWaterDiagnostic==6)above=vec3(foam);
 fragColor=vec4(above,1);
}

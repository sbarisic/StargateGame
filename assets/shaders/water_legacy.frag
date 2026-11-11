#version 400 core
#include "terrain_coverage.glsl"
in vec3 vWorld;in vec2 vUv;in float vWaterDepth;out vec4 fragColor;
uniform sampler2D uOpaqueColor,uOpaqueDepth;uniform vec2 uTargetSize;uniform mat4 uInverseViewProjection,uView,uViewProjection;uniform vec3 uViewPosition;uniform float uTime;uniform int uWaterReflections;
uniform float uUnderwater;
#include "daylight.glsl"
#include "shadows.glsl"
#include "gate_lighting.glsl"
vec3 reconstruct(vec2 uv,float depth){vec4 p=uInverseViewProjection*vec4(uv*2-1,depth*2-1,1);return p.xyz/p.w;}
vec2 ripples(vec2 q){
 return vec2(sin(dot(q,vec2(2.8,1.4))+uTime*.65),cos(dot(q,vec2(-1,3.6))-uTime*.5))*.025
       +vec2(cos(dot(q,vec2(8.2,4.8))-uTime*1.1),sin(dot(q,vec2(-5.4,7.6))+uTime*.9))*.012;
}
vec3 reflectionColor(vec3 direction){
 // Filter the environment over a small roughness cone. A perfect sky lookup
 // made moving rivers look like polished chrome, with hard cloud silhouettes.
 float roughness=mix(.025,.12,clamp(length(vUv),0,1));
 vec3 tangent=normalize(cross(direction,abs(direction.y)<.95?vec3(0,1,0):vec3(1,0,0)));
 vec3 fallback=(skyColor(direction)*2+skyColor(normalize(direction+tangent*roughness))+skyColor(normalize(direction-tangent*roughness)))*.25;
 float canopy=surfaceData(vWorld.xz).r;
 fallback=mix(fallback,ambientSkyColor()*.35,clamp(canopy*.7,0,.7));
 if(uWaterReflections==0||direction.y<=.02)return fallback;
 vec3 start=vWorld+vec3(0,.08,0);float lastDelta=-1;
 for(int i=1;i<=24;i++){
  float distance=float(i)*float(i)*.12;vec3 point=start+direction*distance;vec4 clip=uViewProjection*vec4(point,1);vec2 uv=clip.xy/clip.w*.5+.5;
  if(clip.w<=0||any(lessThan(uv,vec2(.01)))||any(greaterThan(uv,vec2(.99))))break;
  float depth=texture(uOpaqueDepth,uv).r;if(depth>=.99999)continue;
  vec3 scene=reconstruct(uv,depth);float delta=-(uView*vec4(point,1)).z+(uView*vec4(scene,1)).z;
  if(delta>=0&&delta<.45&&lastDelta<0&&scene.y>vWorld.y+.08){float edge=smoothstep(0,.12,min(min(uv.x,1-uv.x),min(uv.y,1-uv.y)))*(1-smoothstep(.1,.45,delta));return mix(fallback,applyFog(texture(uOpaqueColor,uv).rgb,scene,vWorld),edge*mix(.8,.3,clamp(length(vUv),0,1)));}
  lastDelta=delta;
 }
 return fallback;
}
void main(){terrainCoverage(vWorld);
 vec2 uv=gl_FragCoord.xy/uTargetSize;float depth=texture(uOpaqueDepth,uv).r;vec3 bed=reconstruct(uv,depth);
 if(vWaterDepth<.001)discard;
 vec3 view=normalize(uViewPosition-vWorld);float vertical=max(0,vWaterDepth),thickness=depth<.99999?length(bed-vWorld):80.0;
 vec2 q=vWorld.xz;float shore=smoothstep(0,.20,vertical);
 // Two overlapping travel phases prevent UV stretching as time accumulates.
 // Flow is interpolated across shared vertices, not restarted per triangle.
 float phase=fract(uTime/6.0),other=fract(phase+.5),blend=abs(phase-.5)*2;
 vec2 slope=mix(ripples(q-vUv*phase*.75),ripples(q-vUv*other*.75),blend);
 slope*=shore/(1+length(fwidth(q))*5);
 // The clipped shoreline is not a wave. Using each clipped triangle's normal
 // made metre-grid bank triangles flash as large silver facets. Use the smooth
 // downstream current for the mean normal, with small advected ripples.
 vec3 n=normalize(vec3(vUv.x*.06+slope.x,1,vUv.y*.06+slope.y));if(dot(n,view)<0)n=-n;
 vec2 refractUv=clamp(uv+slope*.018*shore,vec2(.001),vec2(.999));vec3 refractBed=reconstruct(refractUv,texture(uOpaqueDepth,refractUv).r);
 if(dot(refractBed-uViewPosition,vWorld-uViewPosition)<dot(vWorld-uViewPosition,vWorld-uViewPosition)||refractBed.y>vWorld.y+.02)refractUv=uv;
 float refractDepth=texture(uOpaqueDepth,refractUv).r;
 thickness=refractDepth<.99999?length(reconstruct(refractUv,refractDepth)-vWorld):80.0;
 thickness=min(thickness,vertical/max(abs(view.y),.08));
 vec3 transmitted=texture(uOpaqueColor,refractUv).rgb;vec3 absorption=vec3(.30,.12,.09),scatter=vec3(.035,.08,.065)*max(.12,length(ambientSkyColor()));
 vec3 transmission=exp(-absorption*thickness);transmitted=transmitted*transmission+scatter*(1-transmission);
 float fresnel=.02+.98*pow(1-max(dot(view,n),0),5);vec3 reflected=reflectionColor(reflect(-view,n));
 float visibility=cloudVisibility(vWorld)*SampleSunVisibility(vWorld,vec3(0,1,0),max(n.y,0));
 vec3 color=mix(transmitted,reflected,fresnel);color+=sunLightColor()*pow(max(dot(reflect(-uSunDirection,n),view),0),220)*visibility*.7;
 float foam=(1-smoothstep(.03,.22,vertical))*shore*(.5+.5*sin(q.x*11+sin(q.y*8)))*.12;color=mix(color,ambientSkyColor(),foam);
 color+=gateLighting(vWorld,n,view,vec3(.025,.07,.09),.28);
 color=mix(texture(uOpaqueColor,uv).rgb,color,smoothstep(0,.06,vertical));
 vec3 above=applyFog(color,vWorld,uViewPosition);
 if(uUnderwater>0.0){
  // From below, transmit the above-water scene and attenuate only the camera-to-surface segment.
  float cosine=max(dot(view,n),0);float totalInternal=1-smoothstep(.64,.68,cosine);
  vec3 underside=mix(texture(uOpaqueColor,refractUv).rgb,skyColor(vec3(0,-1,0)),max(fresnel,totalInternal));
  vec3 t=exp(-absorption*length(uViewPosition-vWorld));color=underside*t+scatter*(1-t);
  fragColor=vec4(mix(above,color,uUnderwater),1);
 }else {
  // An uphill river may be higher than the eye while the camera is on dry land.
  // Only the shared camera water query may select underwater transport.
  fragColor=vec4(above,1);
 }
}

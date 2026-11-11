#version 400 core
uniform sampler2DArray uFoliageColor,uFoliageNormal,uFoliageMasks;uniform int uMaterial,uDetailed;
uniform int uRegionalPlant;
uniform vec3 uViewPosition;uniform mat4 uView;
in vec3 vWorld,vNormal;flat in vec3 vRoot,vGroundNormal;flat in vec4 vGroundMaterial;in vec2 vUv;in float vFade,vShade,vStem;layout(location=0) out vec4 fragColor;
#include "ao_outputs.glsl"

#include "daylight.glsl"
#include "shadows.glsl"
#include "gate_lighting.glsl"
#include "meadow.glsl"
#include "terrain_pbr.glsl"
void main(){
 vec4 tex=texture(uFoliageColor,vec3(vUv,uMaterial));
 if(uRegionalPlant==1){float cluster=surfaceNoise(vRoot.xz/14.,uint(uSurfaceSeed));tex=vec4(vUv.x>.5?mix(vec3(.28,.18,.65),vec3(.92,.85,.48),step(.55,cluster)):vec3(.12,.28,.06),1);}
 if(uRegionalPlant==2)tex=vec4(vec3(.27,.19,.12)*(.85+.15*sin(vWorld.x*30.+vWorld.z*19.)),1);
 if(tex.a<.4)discard;
 float dither=fract(52.9829189*fract(dot(gl_FragCoord.xy,vec2(.06711056,.00583715))));if(uDetailed!=0?dither>=vFade:dither<1-vFade)discard;
 vec3 g=normalize(vNormal);if(!gl_FrontFacing)g=-g;
 vec3 dp1=dFdx(vWorld),dp2=dFdy(vWorld);vec2 uv1=dFdx(vUv),uv2=dFdy(vUv);vec3 a=cross(dp2,g),b=cross(g,dp1);
 vec3 t=a*uv1.x+b*uv2.x,bt=a*uv1.y+b*uv2.y;float scale=inversesqrt(max(max(dot(t,t),dot(bt,bt)),1e-8));
 vec3 nm=texture(uFoliageNormal,vec3(vUv,uMaterial)).xyz*2-1;nm.y=-nm.y;vec3 n=normalize(mat3(t*scale,bt*scale,g)*nm);
 // Thin leaves collect light from the sky on either side. Keep the geometric
 // normal for the tangent frame, but prevent back faces becoming downward-facing slabs.
 if(uRegionalPlant>0)n=g;
 n=normalize(mix(g,n,.55));n=normalize(vec3(n.x,max(abs(n.y),.25),n.z));
 TerrainSurface ground=terrainMaterial(vRoot,normalize(vGroundNormal),vGroundMaterial,10000.);
 bool grassy=uRegionalPlant==0&&(uMaterial==0||uMaterial>=3);
 vec3 plant=decodeColor(tex.rgb)*vShade*(grassy?1.:1.18);
 if(grassy)plant=mix(vec3(dot(plant,vec3(.2126,.7152,.0722))),plant,.65)*vec3(1.03,1.,.85);
 vec3 base=mix(plant,ground.color,mix(grassy?.25:.35,grassy?.08:.15,smoothstep(0,.55,vStem)))*mix(.65,1.0,smoothstep(0,.45,vStem));
 if(uRegionalPlant>0)base=decodeColor(tex.rgb);
 float rough=max(.8,texture(uFoliageMasks,vec3(vUv,uMaterial)).g);
 float visibility=cloudVisibility(vWorld)*SampleSunVisibility(vWorld,normalize(vGroundNormal),max(uSunDirection.y,0.));
 vec3 ambient=base*mix(ambientGroundColor(),ambientSkyColor(),.70);
 vec3 light=ambient+base*(sunLightColor()*(max(dot(n,uSunDirection),0)+.2*max(dot(-n,uSunDirection),0))*visibility);
 writeAmbient(ambient,g,grassy?3.:2.);
 light+=gateLighting(vWorld,n,normalize(uViewPosition-vWorld),base,rough);fragColor=vec4(applyFog(light,vWorld,uViewPosition),1);
}

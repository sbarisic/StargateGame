#version 400 core
#include "terrain_coverage.glsl"
uniform sampler2D uTexture;
uniform sampler2D uGrass,uCliff,uSand,uSnow;
uniform sampler2D uGrassNormal,uCliffNormal,uSandNormal,uSnowNormal;
uniform sampler2D uModelNormal,uMetallicRoughness,uModelEmission;
uniform vec3 uModelEmissionGain;
uniform int uHasMetallicRoughness;
uniform float uModelHeightScale,uModelParallaxRange;
uniform int uModelParallaxSteps;
uniform int uHasModelNormal;
uniform int uRockSnow,uTreeMode;
flat in vec2 vTreeRange;uniform float uLeafTransmission,uLeafOlive,uLeafSaturation;uniform vec3 uLeafTint;
in vec3 vTreeLocal;
uniform float uFoliageCutoff;
uniform int uTerrain,uLandscapeView;
uniform vec3 uTint,uViewPosition;
uniform float uRoughness;
uniform int uDhdMatte;
uniform int uArmSkin;
uniform mat4 uView;
sample in vec3 vWorld,vNormal;
sample in vec2 vUv;
in float vDistance;
in vec4 vMaterial;
#include "gbuffer_outputs.glsl"

#include "daylight.glsl"
#include "snow_surface.glsl"
#include "meadow.glsl"
#include "terrain_pbr.glsl"
#include "rifle_finish.glsl"
vec3 terrainSample(sampler2D surface,float metres,vec3 weights){
 vec3 p=vWorld/metres;
 return texture(surface,p.zy).rgb*weights.x+texture(surface,p.xz).rgb*weights.y+texture(surface,p.xy).rgb*weights.z;
}
vec3 unpackNormal(vec3 n){vec2 xy=(n.xy*2.0-1.0)*vec2(1,-1); return vec3(xy,sqrt(max(1.0-dot(xy,xy),.01)));}
// Convert each projection's perturbation to world space. A neutral map returns the geometric normal.
vec3 terrainNormal(sampler2D surface,float metres,vec3 weights,vec3 g){
 vec3 p=vWorld/metres;
 vec3 x=unpackNormal(texture(surface,p.zy).rgb),y=unpackNormal(texture(surface,p.xz).rgb),z=unpackNormal(texture(surface,p.xy).rgb);
 vec3 perturb=vec3(0,x.y,x.x)*weights.x+vec3(y.x,0,y.y)*weights.y+vec3(z.x,z.y,0)*weights.z;
 perturb-=g*dot(perturb,g);
 return normalize(g+perturb*.55*(1.0-smoothstep(35.0,140.0,vDistance)));
}
// Material alpha carries scanned displacement. Bound the ray length and fade
// relief at distance/grazing angles; this changes shading, not collision/silhouette.
// Height-field light ray inspired by LiveTrower's CC0 shader:
// https://godotshaders.com/shader/parallax-occlusion-mapping-with-self-shadowing/
float modelReliefVisibility=1.;
float modelHitDepth=0.;
uniform int uDeferredReceiver;
float modelHeightShadow(vec2 uv,vec2 dx,vec2 dy,vec2 lightUv,float facing,float strength){
 if(facing<=0.)return 0.;
 float start=1.-textureGrad(uMetallicRoughness,uv,dx,dy).a;
 int steps=int(mix(float(uModelParallaxSteps),8.,clamp(facing,0.,1.)));
 vec2 ray=lightUv/max(facing,.08)*strength;
 float visibility=1.;
 for(int i=1;i<=48;i++){
  if(i>steps)break;
  float rise=start*float(i)/float(steps);
  float surface=1.-textureGrad(uMetallicRoughness,uv+ray*rise,dx,dy).a;
  visibility=min(visibility,1.-smoothstep(.01,.04,start-rise-surface));
  if(visibility<=0.)break;
 }
 return visibility;
}
vec2 modelReliefUv(vec2 uv,vec3 g){
 if(uModelHeightScale<=0.0||uHasMetallicRoughness==0||uModelParallaxSteps==0)return uv;
 vec3 p1=dFdx(vWorld),p2=dFdy(vWorld);vec2 dx=dFdx(uv),dy=dFdy(uv);
 vec3 c1=cross(p2,g),c2=cross(g,p1);
 float determinant=dot(p1,c1);
 if(abs(determinant)<1e-12)return uv;
 // UV gradients per world metre preserve relief depth across authored UV
 // scales and scaled/instanced meshes, including mirrored texture islands.
 vec3 gradU=(c1*dx.x+c2*dy.x)/determinant;
 vec3 gradV=(c1*dx.y+c2*dy.y)/determinant;
 vec3 view=normalize(uViewPosition-vWorld);
 float facing=abs(dot(view,g));
 float strength=uModelHeightScale*(1.-smoothstep(uModelParallaxRange*.35,uModelParallaxRange,vDistance))*smoothstep(.035,.16,facing);
 if(strength<.00001)return uv;
 vec2 ray=vec2(dot(view,gradU),dot(view,gradV))/max(facing,.15)*strength;
 int steps=int(mix(float(uModelParallaxSteps),10.,clamp(facing,0.,1.)));
 float stepDepth=1./float(steps),depth=0.;vec2 at=uv;
 float sampled=1.-textureGrad(uMetallicRoughness,at,dx,dy).a;
 for(int i=0;i<48;i++){
  if(i>=steps||depth>=sampled)break;
  depth+=stepDepth;at-=ray*stepDepth;
  sampled=1.-textureGrad(uMetallicRoughness,at,dx,dy).a;
 }
 bool topHit=depth==0.;
 float low=max(0.,depth-stepDepth),high=depth;
 for(int i=0;i<5;i++){
  float mid=(low+high)*.5;
  float surface=1.-textureGrad(uMetallicRoughness,uv-ray*mid,dx,dy).a;
  if(mid<surface)low=mid;else high=mid;
 }
 vec2 hitUv=topHit?uv:uv-ray*((low+high)*.5);
 modelHitDepth=topHit?0.:(low+high)*.5*strength;
 if(!topHit)modelReliefVisibility=modelHeightShadow(hitUv,dx,dy,
     vec2(dot(uSunDirection,gradU),dot(uSunDirection,gradV)),dot(uSunDirection,g),strength);
 return hitUv;
}
uniform vec4 uTravelPlane;
void main(){terrainCoverage(vWorld);
    if(dot(vec4(vWorld,1.0),uTravelPlane)<0.0)discard;
 if(uTreeMode>0){float d=fract(sin(dot(floor(vTreeLocal*96.),vec3(12.9898,78.233,39.425)))*43758.5453);if(d<vTreeRange.x||d>=vTreeRange.y)discard;}
 vec2 modelDx=dFdx(vUv),modelDy=dFdy(vUv);
 vec2 modelUv=modelReliefUv(vUv,normalize(vNormal));
 vec4 surface=textureGrad(uTexture,modelUv,modelDx,modelDy);
 if(uFoliageCutoff>0.0&&surface.a<uFoliageCutoff)discard;
 vec3 g=normalize(vNormal),n=g;
 if((uFoliageCutoff>0.0||uTreeMode==2)&&!gl_FrontFacing){g=-g;n=g;}
 if(uHasModelNormal!=0){
  vec3 dp1=dFdx(vWorld),dp2=dFdy(vWorld);vec2 duv1=dFdx(vUv),duv2=dFdy(vUv);
  vec3 perpendicular1=cross(dp2,g),perpendicular2=cross(g,dp1);
  vec3 tangent=perpendicular1*duv1.x+perpendicular2*duv2.x;
  vec3 bitangent=perpendicular1*duv1.y+perpendicular2*duv2.y;
  float invScale=inversesqrt(max(max(dot(tangent,tangent),dot(bitangent,bitangent)),1e-10));
  vec3 detail=unpackNormal(textureGrad(uModelNormal,modelUv,modelDx,modelDy).rgb);
  n=normalize(mat3(tangent*invScale,bitangent*invScale,g)*detail);
 }
 float snowCoverage=0.0;vec2 snowUv=vWorld.xz;
 if(uRockSnow!=0)snowUv=snowReliefUv(vWorld,uViewPosition,g);
 vec3 base=surface.rgb*uTint;
 if(uTerrain==1)base=vec3(.30,.36,.20);
 TerrainSurface terrainPbrSurface;
 if(uTerrain==2 && uLandscapeView>0){writeSurface(vec3(0),1.,g,0.,0.,g,0.,9.,vMaterial.rgb,1.,gl_FragCoord.z,gl_FragCoord.z);return;}
 if(uTerrain==2){
  terrainPbrSurface=terrainMaterial(vWorld,g,vMaterial,vDistance,vUv);base=terrainPbrSurface.color;n=terrainPbrSurface.normal;
  if(uMeadowEnabled!=0){
   // Beyond the patch renderer, represent subpixel blades with continuous
   // meadow reflectance. Regional fields exist even on far terrain tiles.
   vec4 ecology=surfaceData(vWorld.xz);
   float cover=ecology.b> -4000.0?ecology.a:1.0;
   // Use the actual image blend, including height blending and overlays.
   cover*=terrainPbrSurface.grass;
   cover*=mix(1.0,.55,clamp(max(vUv.x,ecology.r),0.0,1.0));
   float meadowPatch=surfacePatch(vWorld.xz);
   cover*=mix(.65,.95,smoothstep(.22,.65,meadowPatch));
   float farBlend=smoothstep(uMeadowRange*.65,uMeadowRange,vDistance);
   vec3 meadow=mix(vec3(.075,.105,.028),vec3(.14,.19,.055),meadowPatch);
   base=mix(base,meadow,cover*farBlend);terrainPbrSurface.rough=mix(terrainPbrSurface.rough,.96,cover*farBlend);
  }
  snowCoverage=uTerrainBiome==2?(1-smoothstep(.30,.82,vMaterial.g)):0;
 }
 // Building plaster uses metre-scaled UVs and its own albedo/normal maps.
 // No artificial per-tile grid: connected walls read as a continuous surface.
 if(uTerrain!=2)base=decodeColor(clamp(base,0.0,1.0));
 if(uRockSnow!=0){
  vec3 stone=vec3(dot(base,vec3(.2126,.7152,.0722)))*vec3(.77,.83,.90);
  float coverage=smoothstep(.55,.92,g.y)*(.75+.10*sin(vWorld.x*4.0+sin(vWorld.z*5.0)));
  base=mix(stone,decodeColor(snowAlbedo(uSnow,snowUv)),coverage);n=normalize(mix(n,snowDetailNormal(snowUv,g,vDistance),coverage));snowCoverage=coverage;
 }
 float rifleRoughness=.54,rifleMetal=0.;
 if(uRifleFinish!=0)rifleFinish(vUv,vWorld,base,n,rifleRoughness,rifleMetal);
 float rough=uTerrain>0?.88:uRoughness;
 if(snowCoverage>0.0)rough=mix(rough,mix(.72,.94,snowNoise(snowUv*3.2)),snowCoverage);
 if(uTerrain==2)rough=terrainPbrSurface.rough;
 float metal=0.0;
 if(uHasMetallicRoughness!=0){vec3 mr=textureGrad(uMetallicRoughness,modelUv,modelDx,modelDy).rgb;rough=clamp(mr.g,.12,1.0);metal=mr.b;}
 if(uRifleFinish!=0){rough=rifleRoughness;metal=rifleMetal;}
 if(uArmSkin!=0){
  // Keep the source texture variation without waxy cloth/skin highlights.
  rough=uHasMetallicRoughness==0?.82:max(.74,rough);
  metal=0.;
 }
 if(uTreeMode==2){rough=max(.90,rough);metal=0.;vec3 olive=mix(vec3(dot(base,vec3(.2126,.7152,.0722))),base,uLeafSaturation)*uLeafTint;base=mix(base,olive,uLeafOlive);}
 float spec=uDhdMatte!=0?.18:uTreeMode==2?.18:uArmSkin!=0?.30:uRifleFinish!=0?.45:1.;
 float kind=uDhdMatte!=0?float(uDeferredReceiver):uTerrain==2?4.:uTreeMode==2?5.:uRifleFinish!=0?6.:uArmSkin!=0?7.:snowCoverage>.5?8.:1.;
 vec3 receiver=normalize(cross(dFdx(vWorld),dFdy(vWorld)));if(dot(receiver,g)<0.)receiver=-receiver;
 vec3 ray=normalize(uViewPosition-vWorld);
 float distanceAlongRay=uTerrain==2?terrainPbrSurface.depth:modelHitDepth/max(abs(dot(ray,g)),.15);
 if(uRockSnow!=0)distanceAlongRay=mix(distanceAlongRay,snowHitDistance,snowCoverage);
 vec3 hit=vWorld-ray*distanceAlongRay;
 float depth=projectedSurfaceDepth(hit,gl_FragCoord.z);
 vec3 emission=dot(uModelEmissionGain,uModelEmissionGain)>0.?textureGrad(uModelEmission,modelUv,modelDx,modelDy).rgb*uModelEmissionGain:vec3(0);
 writeSurface(base,rough,n,metal,spec,receiver,uTreeMode==2?uLeafTransmission:snowCoverage,kind,emission,uTerrain==2?terrainPbrSurface.shadow:modelReliefVisibility,gl_FragCoord.z,max(gl_FragCoord.z,depth));
}

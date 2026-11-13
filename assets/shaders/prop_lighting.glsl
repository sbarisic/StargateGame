#include "point_light_common.glsl"
uniform int uPropLightCount,uPropShadowCount,uPropShadows,uPropShadowMask;
uniform int uPropMuzzleSpillMask;
uniform sampler2D uPropShadowAtlas;
uniform vec4 uPropPositionRange[16],uPropColorRadius[16];
uniform vec2 uPropAtlasSize;
uniform float uPropTileSize;
vec3 propLightingParts(vec3 p,vec3 shadowPosition,vec3 geometricNormal,vec3 n,vec3 v,vec3 base,float rough,float specStrength,float metal,float transmission,out vec3 specular,out vec3 transmitted){
 vec3 sum=vec3(0);specular=vec3(0);transmitted=vec3(0);
 for(int i=0;i<uPropLightCount;i++){
  vec3 delta=uPropPositionRange[i].xyz-p;float distance=length(delta),range=uPropPositionRange[i].w;
  if(distance>=range||distance<.0001)continue;
  vec3 toward=delta/distance;
  // Shadow casters contain geometric surfaces, not POM recesses. Testing the
  // recessed hit would put floors/ceilings behind their own shadow caster.
  vec3 receiver=dot(geometricNormal,toward)<0.?-geometricNormal:geometricNormal;
  vec3 shadowDelta=shadowPosition+receiver*.008+toward*.004-uPropPositionRange[i].xyz;
  int face=i*6+pointFace(shadowDelta);
  float visibility=uPropShadows==0||(uPropShadowMask&(1<<i))==0?1.:pointShadowClipVisibility(uPropShadowAtlas,pointShadowClip(shadowDelta,face%6,range,uPropTileSize),face,uPropShadowCount*6,uPropAtlasSize,uPropTileSize);
  vec3 irradiance=uPropColorRadius[i].rgb*pointAttenuation(distance,range,uPropColorRadius[i].w)*visibility;
  vec3 spec;sum+=pointResponse(irradiance,n,toward,v,base,rough,specStrength,metal,spec);specular+=spec;
  if((uPropMuzzleSpillMask&(1<<i))!=0){
   // Bounded diffuse return from the extended flash and nearby surfaces. This
   // is an artistic indirect term, not a second unshadowed world light; direct
   // shadows and highlights still use the muzzle's cube shadow map above.
   float nearField=1.-smoothstep(1.,1.6,distance);
   float bounce=.18*nearField*mix(.65,1.,clamp(n.y*.5+.5,0.,1.));
   sum+=base*(1.-metal*.45)*uPropColorRadius[i].rgb*
        pointAttenuation(distance,range,max(uPropColorRadius[i].w,.15))*bounce;
  }
  transmitted+=base*irradiance*transmission*max(dot(-n,toward),0.);
 }
 return sum;
}
vec3 propLighting(vec3 p,vec3 n,vec3 v,vec3 base,float rough){
 vec3 spec,trans;vec3 diffuse=propLightingParts(p,p,n,n,v,base,rough,1.,0.,0.,spec,trans);return diffuse+spec+trans;
}

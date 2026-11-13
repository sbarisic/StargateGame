uniform vec3 uViewPosition;uniform mat4 uView;
#include "daylight.glsl"
#include "shadows.glsl"
#include "gate_lighting.glsl"
#include "prop_lighting.glsl"
#include "dhd_lighting.glsl"
#include "rifle_lighting.glsl"
uniform int uInspection,uWeapon,uDhdReceiver,uPointOnly;
uniform float uGroundFillWeight;
uniform float uStrength,uAoRadius;uniform int uEnabled;
uniform sampler2D uAo;
uniform mat4 uInverseViewProjection;
vec3 reconstruct(vec2 uv,float depth){vec4 p=uInverseViewProjection*vec4(uv*2.-1.,depth*2.-1.,1);return p.xyz/p.w;}
vec3 deferredSky(vec3 ray){
 vec3 color=skyColor(ray);
 // Ground-level horizon haze must not cover clouds seen from above. Their
 // own aerial perspective already accounts for the air up at cloud altitude.
 float groundHaze=exp(-max(uViewPosition.y-uGroundLevel,0.)*.002);
 float haze=max(uOvercast,1.-smoothstep(.01,.10,ray.y))*groundHaze;
 if(haze>0.)color=mix(color,applyFog(color,uViewPosition+ray*6500.,uViewPosition),haze);
 return color;
}
// Preserve the old quality/receiver policy, but apply it before lighting.
float aoReceiver(float kind){float k=aoClass(kind);return k==2.?.25:k==3.?.35:1.;}
vec3 deferredLight(vec2 uv,vec4 a,vec4 s,vec4 g,vec4 e,float baseDepth,float depth,float ao){
 vec3 p=reconstruct(uv,depth),basePosition=reconstruct(uv,baseDepth);
 vec3 n=octDecode(s.xy),gn=octDecode(g.xy),v=normalize(uViewPosition-p);
 float kind=floor(g.a+.5),rough=a.a,metal=s.z,specStrength=s.w;
 vec3 base=a.rgb;
 if(uPointOnly!=0){
  vec3 spec,trans;
  vec3 diffuse=propLightingParts(p,basePosition,gn,n,v,base,rough,specStrength,metal,(kind==2.||kind==3.||kind==5.)?g.b:0.,spec,trans);
  if(uInspection==11)return spec;
  if(uInspection==10)return trans;
  return diffuse+spec+trans;
 }
 if(uInspection==14)return vec3(rough);
 if(uInspection==15)return vec3(metal);
 if(uInspection==16)return vec3(specStrength);
 if(uInspection==17)return vec3(g.b);
 if(uInspection==18)return vec3(e.a);
 if(uDhdReceiver>0){
  if(kind!=float(uDhdReceiver))return vec3(0);
  vec3 localSpec;vec3 localDiffuse=dhdLightingParts(basePosition,gn,n,v,base,rough,specStrength,localSpec);
  return uInspection==11?localSpec:localDiffuse+localSpec;
 }
 float visibility=uEnabled==0?1.:1.-clamp((1.-ao)*uStrength*aoReceiver(kind),0.,1.);
 float diffuseAo=.5+.5*visibility;
 bool grass=kind==3.,leaf=kind==2.,canopy=kind==5.;
 vec3 lightNormal=grass?normalize(vec3(n.x,abs(n.y)+.3,n.z)):n;
 float nl=canopy?abs(dot(n,uSunDirection)):max(dot(lightNormal,uSunDirection),0.);
 if(!grass&&!leaf&&!canopy)nl=mix(nl,max(0.,(dot(n,uSunDirection)+.18)/1.18),g.b);
 vec3 shadowNormal=canopy && dot(gn,uSunDirection)<0.?-gn:gn;
 vec3 offsetNormal=kind==7.&&uViewmodelShadowEnabled!=0?n:shadowNormal;
 float shadow=SampleSunVisibilityNormals(basePosition,offsetNormal,shadowNormal,max(dot(shadowNormal,uSunDirection),0.));
 shadow=(canopy?min(shadow,e.a):shadow*e.a)*cloudVisibility(basePosition);
 vec3 ambient=mix(ambientGroundColor(),ambientSkyColor(),grass?.78:canopy?1.-uGroundFillWeight:n.y*.5+.5);
 vec3 indirect=base*ambient*(1.-metal*.45)*visibility;
 vec3 diffuse=base*(1.-metal)*nl;
 vec3 halfVector=v+uSunDirection;vec3 h=halfVector*inversesqrt(max(dot(halfVector,halfVector),1e-8));float power=mix(96.,6.,rough*rough);
 vec3 specular=mix(vec3(.04),base,metal)*specStrength*pow(max(dot(n,h),0.),power)*(power+2.)/8.*nl;
 if(kind==4.||kind==6.){
  float nv=max(dot(n,v),.001),lh=max(dot(v,h),0.),nh=max(dot(n,h),0.);
  float alpha=kind==6.?rough*rough:max(.06,rough*rough),a2=alpha*alpha;
  float distribution=a2/(3.14159265*pow(nh*nh*(a2-1.)+1.,2.));
  float k=pow(rough+1.,2.)/8.;float geometry=nv/(nv*(1.-k)+k)*nl/max(nl*(1.-k)+k,.001);
  vec3 f0=mix(vec3(.04),base,metal),f=f0+(1.-f0)*pow(1.-lh,5.);
  diffuse=(1.-f)*base*(1.-metal)*nl;
  specular=distribution*geometry*f/max(4.*nv*nl,.001)*nl*3.14159265;
  indirect=base*ambient*(1.-metal)*visibility;
 }
 float transmission=canopy?g.b*pow(max(dot(-v,uSunDirection),0.),3.)*(1.-nl):(grass||leaf?g.b*max(dot(-n,uSunDirection),0.):0.);
 vec3 sun=sunLightColor()*shadow;
 diffuse*=sun*diffuseAo;specular*=sun;
 vec3 transmitted=base*sun*transmission*diffuseAo;
 vec3 localSpec;vec3 local=gateLightingParts(p,basePosition,gn,n,v,base,rough,specStrength,metal,localSpec);
 specular+=localSpec;
 if(canopy)local+=gateLighting(p,-n,v,base,rough,0.);
 if(!grass&&!leaf&&!canopy)specular+=sun*g.b*pow(max(dot(n,h),0.),160.)*.025*(1.-smoothstep(3.,14.,length(p-uViewPosition)));
 if(kind==6.)specular+=rifleEnvironment(base,n,v,rough,metal);
 vec3 lit=indirect+diffuse+transmitted+specular+local+e.rgb;
 if(kind==9.)lit=e.rgb;
 if(uInspection==1)return vec3(visibility);
 if(uInspection==2)return base;
 if(uInspection==3)return n*.5+.5;
 if(uInspection==4)return gn*.5+.5;
 if(uInspection==5)return vec3(length(basePosition-uViewPosition)/100.);
 if(uInspection==6)return vec3(length(p-uViewPosition)/100.);
 if(uInspection==7)return fract(vec3(.17,.37,.67)*kind);
 if(uInspection==8)return indirect;
 if(uInspection==9)return diffuse;
 if(uInspection==10)return transmitted;
 if(uInspection==11)return specular;
 if(uInspection==12)return e.rgb;
 return lit;
}

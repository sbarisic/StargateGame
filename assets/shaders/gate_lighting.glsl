uniform int uGateLightCount;
uniform sampler2D uGateShadowAtlas;
uniform vec4 uGatePositionRange[8],uGateColorRadius[8];
uniform mat4 uGateMatrices[48];
uniform vec2 uGateAtlasSize;
uniform float uGateTileSize;
#include "point_light_common.glsl"
float gateVisibility(int light,vec3 p,vec3 normal,vec3 toward){
 vec3 receiver=p+normal*.008+toward*.004;
 int face=light*6+pointFace(receiver-uGatePositionRange[light].xyz);
 return pointShadowClipVisibility(uGateShadowAtlas,uGateMatrices[face]*vec4(receiver,1.),face,48,uGateAtlasSize,uGateTileSize);
}
vec3 gateLightingParts(vec3 p,vec3 shadowPosition,vec3 geometricNormal,vec3 normal,vec3 view,vec3 albedo,float rough,float specularStrength,float metal,out vec3 specular){
 vec3 sum=vec3(0);specular=vec3(0);
 for(int i=0;i<uGateLightCount;i++){
  if(dot(uGateColorRadius[i].rgb,uGateColorRadius[i].rgb)==0.0)continue;
  vec3 delta=uGatePositionRange[i].xyz-p;float distance=length(delta),range=uGatePositionRange[i].w;
  if(distance>=range||distance<.0001)continue;
  vec3 toward=delta/distance;float diffuse=max(dot(normal,toward),0.0);
  if(diffuse<=0.0)continue;
  float radius=uGateColorRadius[i].w;
  vec3 receiver=dot(geometricNormal,toward)<0.?-geometricNormal:geometricNormal;
  vec3 irradiance=uGateColorRadius[i].rgb*pointAttenuation(distance,range,radius)*gateVisibility(i,shadowPosition,receiver,toward);
  vec3 spec;sum+=pointResponse(irradiance,normal,toward,view,albedo,rough,specularStrength,metal,spec);specular+=spec;
 }
 return sum;
}
vec3 gateLighting(vec3 p,vec3 normal,vec3 view,vec3 albedo,float rough,float specularStrength){
 vec3 specular;vec3 diffuse=gateLightingParts(p,p,normal,normal,view,albedo,rough,specularStrength,0.,specular);return diffuse+specular;
}
vec3 gateLighting(vec3 p,vec3 normal,vec3 view,vec3 albedo,float rough){
 return gateLighting(p,normal,view,albedo,rough,1.0);
}

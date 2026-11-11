uniform int uGateLightCount;
uniform sampler2D uGateShadowAtlas;
uniform vec4 uGatePositionRange[8],uGateColorRadius[8];
uniform mat4 uGateMatrices[48];
uniform vec2 uGateAtlasSize;
uniform float uGateTileSize;
int gateFace(vec3 d){
 vec3 a=abs(d);
 if(a.x>=a.y&&a.x>=a.z)return d.x>=0?0:1;
 if(a.y>=a.z)return d.y>=0?2:3;
 return d.z>=0?4:5;
}
float gateVisibility(int light,vec3 p,vec3 normal,vec3 toward){
 vec3 delta=p-uGatePositionRange[light].xyz;
 int face=light*6+gateFace(delta);
 // Small receiver bias in world metres, independent of atlas tile resolution.
 vec4 clip=uGateMatrices[face]*vec4(p+normal*.008+toward*.004,1);
 vec3 ndc=clip.xyz/clip.w;
 if(clip.w<=0.0||ndc.z>1.0)return 1.0;
 vec2 pixel=(ndc.xy*.5+.5)*uGateTileSize;
 float compareDepth=ndc.z*.5+.5-.00005;
 float visibility=0.0;
 for(int y=-1;y<=1;y++)for(int x=-1;x<=1;x++){
  vec2 tap=clamp(pixel+vec2(x,y),vec2(.5),vec2(uGateTileSize-.5));
  vec2 st=vec2(face%12,face/12)*uGateTileSize;
  int dynamicFace=face+48;
  vec2 dy=vec2(dynamicFace%12,dynamicFace/12)*uGateTileSize;
  float depth=min(texture(uGateShadowAtlas,(st+tap)/uGateAtlasSize).r,texture(uGateShadowAtlas,(dy+tap)/uGateAtlasSize).r);
  visibility+=compareDepth<=depth?1.0:0.0;
 }
 return visibility/9.0;
}
vec3 gateLighting(vec3 p,vec3 normal,vec3 view,vec3 albedo,float rough,float specularStrength){
 vec3 sum=vec3(0);
 for(int i=0;i<uGateLightCount;i++){
  if(dot(uGateColorRadius[i].rgb,uGateColorRadius[i].rgb)==0.0)continue;
  vec3 delta=uGatePositionRange[i].xyz-p;float distance=length(delta),range=uGatePositionRange[i].w;
  if(distance>=range||distance<.0001)continue;
  vec3 toward=delta/distance;float diffuse=max(dot(normal,toward),0.0);
  if(diffuse<=0.0)continue;
  float radius=uGateColorRadius[i].w;
  float attenuation=pow(max(0.0,1.0-pow(distance/range,4.0)),2.0)/(distance*distance+radius*radius);
  float power=mix(96.0,6.0,rough*rough);
  float spec=pow(max(dot(normal,normalize(view+toward)),0.0),power)*.04*(power+2.0)/8.0;
  sum+=(albedo+vec3(spec*specularStrength))*uGateColorRadius[i].rgb*diffuse*attenuation*gateVisibility(i,p,normal,toward);
 }
 return sum;
}
vec3 gateLighting(vec3 p,vec3 normal,vec3 view,vec3 albedo,float rough){
 return gateLighting(p,normal,view,albedo,rough,1.0);
}

#ifndef POINT_LIGHT_COMMON
#define POINT_LIGHT_COMMON
int pointFace(vec3 d){
 vec3 a=abs(d);
 if(a.x>=a.y&&a.x>=a.z)return d.x>=0?0:1;
 if(a.y>=a.z)return d.y>=0?2:3;
 return d.z>=0?4:5;
}
float pointAttenuation(float distance,float range,float radius){
 return pow(max(0.,1.-pow(distance/range,4.)),2.)/(distance*distance+radius*radius);
}
float pointShadowClipVisibility(sampler2D atlas,vec4 clip,int face,int dynamicOffset,vec2 atlasSize,float tileSize){
 vec3 ndc=clip.xyz/clip.w;
 if(clip.w<=0.||ndc.z>1.)return 1.;
 vec2 pixel=(ndc.xy*.5+.5)*tileSize;
 float compareDepth=ndc.z*.5+.5-.00005,visibility=0.;
 for(int y=-1;y<=1;y++)for(int x=-1;x<=1;x++){
  vec2 tap=clamp(pixel+vec2(x,y),vec2(.5),vec2(tileSize-.5));
  vec2 st=vec2(face%12,face/12)*tileSize;
  int dynamicFace=face+dynamicOffset;
  vec2 dy=vec2(dynamicFace%12,dynamicFace/12)*tileSize;
  float depth=min(texture(atlas,(st+tap)/atlasSize).r,texture(atlas,(dy+tap)/atlasSize).r);
  visibility+=compareDepth<=depth?1.:0.;
 }
 return visibility/9.;
}
// Six fixed cube views can project analytically, avoiding 48 extra matrix uniforms.
vec4 pointShadowClip(vec3 delta,int face,float range,float tileSize){
 vec3 v=face==0?vec3(-delta.z,-delta.y,delta.x):face==1?vec3(delta.z,-delta.y,-delta.x):
        face==2?vec3(delta.x,delta.z,delta.y):face==3?vec3(delta.x,-delta.z,-delta.y):
        face==4?vec3(delta.x,-delta.y,delta.z):vec3(-delta.x,-delta.y,-delta.z);
 float nearPlane=.04,scale=(tileSize-4.)/tileSize;
 // Match System.Numerics.CreatePerspectiveFieldOfView used by PointShadowView.
 return vec4(v.xy*scale,range/(range-nearPlane)*v.z-range*nearPlane/(range-nearPlane),v.z);
}
float pointShadowVisibility(sampler2D atlas,mat4 matrix,int face,int dynamicOffset,vec2 atlasSize,float tileSize,vec3 p,vec3 normal,vec3 toward){
 return pointShadowClipVisibility(atlas,matrix*vec4(p+normal*.008+toward*.004,1),face,dynamicOffset,atlasSize,tileSize);
}
vec3 pointResponse(vec3 irradiance,vec3 n,vec3 toward,vec3 v,vec3 base,float rough,float specStrength,float metal,out vec3 spec){
 float diffuse=max(dot(n,toward),0.);
 vec3 hv=v+toward;vec3 h=hv*inversesqrt(max(dot(hv,hv),1e-8));
 float power=mix(96.,6.,rough*rough);
 spec=mix(vec3(.04),base,metal)*specStrength*pow(max(dot(n,h),0.),power)*(power+2.)/8.*diffuse*irradiance;
 return base*(1.-metal)*diffuse*irradiance;
}
#endif

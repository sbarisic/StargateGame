#version 400 core
in vec2 vUv;layout(location=0) out vec4 result;layout(location=1) out float cloudDepth;
uniform sampler2D uLight;uniform vec3 uEye,uSunDirection;uniform int uSteps;uniform float uJitter;
#include "cloud_density.glsl"
#include "ambient_tint.glsl"
vec2 spherical(vec3 d){return vec2(atan(d.z,d.x)/6.2831853+.5,asin(clamp(d.y,-1,1))/3.14159265+.5);}
void main(){
 float az=(vUv.x-.5)*6.2831853,el=(vUv.y-.5)*3.14159265;vec3 ray=vec3(cos(el)*cos(az),sin(el),cos(el)*sin(az));
 cloudDepth=60000;
 if(ray.y<.005){result=vec4(0,0,0,1);return;}
 float start=max(0,(uCloudBase-uEye.y)/ray.y),end=min(60000.0,(uCloudTop-uEye.y)/ray.y);
 if(end<=start){result=vec4(0,0,0,1);return;}
 float dt=(end-start)/float(uSteps),trans=1;vec3 color=vec3(0),sun=texture(uLight,vec2(.25,.5)).rgb,ambient=texture(uLight,vec2(.75,.5)).rgb;
 ambient=scatteringIndirect(ambient,uSunDirection.y);
 float phase=.65+.85*pow(max(dot(ray,uSunDirection),0),8);
 // De-correlate adjacent rays and filter detail below the march spacing;
 // low-angle rays otherwise turn high-frequency erosion into vertical bands.
 float jitter=fract(uJitter+cloudHash(vec3(gl_FragCoord.xy,.713)));
 for(int i=0;i<96;i++){
  if(i>=uSteps||trans<.015)break;
  vec3 p=uEye+ray*(start+(float(i)+jitter)*dt);float density=cloudDensityFiltered(p,dt);if(density<.001)continue;
  float optical=0;for(int j=1;j<=4;j++)optical+=cloudDensityFiltered(p+uSunDirection*(float(j)*180),180.)*180*.005;
  cloudDepth=min(cloudDepth,start+(float(i)+jitter)*dt);
  float t=exp(-density*dt*.005);
  vec2 layer=cloudLayer(p);
  float height=clamp((p.y-layer.x)/(layer.y-layer.x),0.,1.);
  vec3 light=ambient*mix(.16,.48,smoothstep(.05,.8,height))+sun*(phase*exp(-optical)+.12*exp(-optical*.25));
  color+=trans*(1-t)*light;trans*=t;
 }
 vec4 current=mix(vec4(0,0,0,1),vec4(color,trans),smoothstep(.005,.045,ray.y));
 result=current;
}

#version 400 core
in vec2 vUv;layout(location=0) out vec4 result;layout(location=1) out float cloudDepth;
uniform sampler2D uLight;uniform vec3 uEye,uSunDirection;uniform int uSteps;uniform float uJitter;
uniform sampler2D uTrans,uRay,uMie;uniform float uGroundLevel,uSkyRadianceScale;
#include "atmosphere_parameters.glsl"
#include "cloud_density.glsl"
#include "ambient_tint.glsl"
vec2 spherical(vec3 d){return vec2(atan(d.z,d.x)/6.2831853+.5,asin(clamp(d.y,-1,1))/3.14159265+.5);}
void main(){
 float az=(vUv.x-.5)*6.2831853,el=(vUv.y-.5)*3.14159265;vec3 ray=vec3(cos(el)*cos(az),sin(el),cos(el)*sin(az));
 cloudDepth=60000;
 if(abs(ray.y)<.005){result=vec4(0,0,0,1);return;}
 vec2 crossings=vec2(uCloudBase-uEye.y,uCloudTop-uEye.y)/ray.y;
 float start=max(0,min(crossings.x,crossings.y)),end=min(60000.0,max(crossings.x,crossings.y));
 if(end<=start){result=vec4(0,0,0,1);return;}
 float dt=(end-start)/float(uSteps),trans=1;vec3 color=vec3(0),ambient=texture(uLight,vec2(.75,.5)).rgb;
 // Clouds scatter the night-sky fill too. The fog-specific 4% night factor
 // followed by underside darkening made opaque clouds nearly black.
 float daylight=smoothstep(-.16,.03,uSunDirection.y);
 float forward=max(dot(ray,uSunDirection),0.);
 float phase=.55+1.65*pow(forward,16.);
 // De-correlate adjacent rays and filter detail below the march spacing;
 // low-angle rays otherwise turn high-frequency erosion into vertical bands.
 float jitter=fract(uJitter+cloudHash(vec3(gl_FragCoord.xy,.713)));
 for(int i=0;i<96;i++){
  if(i>=uSteps||trans<.015)break;
  vec3 p=uEye+ray*(start+(float(i)+jitter)*dt);float density=cloudDensityFiltered(p,dt);if(density<.001)continue;
  // Short first segments resolve sun-facing lobes; longer later segments
  // measure shadowing through the body without adding lighting samples.
  float optical=0,lightDistance=0;
  for(int j=0;j<4;j++){
   float segment=60.*pow(2.,float(j));
   optical+=cloudDensityFiltered(p+uSunDirection*(lightDistance+segment*.5),segment)*segment*.005;
   lightDistance+=segment;
  }
  cloudDepth=min(cloudDepth,start+(float(i)+jitter)*dt);
  float t=exp(-density*dt*.005);
  vec2 layer=cloudLayer(p);
  float height=clamp((p.y-layer.x)/(layer.y-layer.x),0.,1.);
  float layerLight=smoothstep(.05,.8,height);
  float skyFill=mix(mix(.30,.55,layerLight),mix(.16,.48,layerLight),daylight);
  // Sample the incoming sunlight at cloud altitude, not at the ground: high
  // lobes retain warm light around sunset while the surface is already dark.
  float radius=ATMOSPHERE.bottom_radius+max(.002,(p.y-uGroundLevel)*.001);
  vec3 sun=ATMOSPHERE.solar_irradiance*GetTransmittanceToSun(ATMOSPHERE,uTrans,radius,uSunDirection.y);
  // A bounded multiple-scattering approximation keeps dense interiors from
  // going black, while preserving a bright rim and a darker underside.
  float powder=1.-exp(-density*1.2);
  vec3 light=ambient*skyFill+sun*(phase*exp(-optical)+(.10+.16*powder)*exp(-optical*.25));
  color+=trans*(1-t)*light;trans*=t;
 }
 if(trans<.9999){
  // One aerial-perspective evaluation per ray, using its first cloud depth.
  // Premultiplied cloud light gains haze only over the cloud's opacity.
  vec3 origin=vec3(0,ATMOSPHERE.bottom_radius+max(.002,(uEye.y-uGroundLevel)*.001),0);
  vec3 airTransmission;
  vec3 airLight=GetSkyRadianceToPoint(ATMOSPHERE,uTrans,uRay,uMie,origin,origin+ray*max(.001,cloudDepth*.001),0.,uSunDirection,airTransmission);
  color=color*airTransmission+max(airLight,vec3(0))*uSkyRadianceScale*(1.-trans);
 }
 vec4 current=mix(vec4(0,0,0,1),vec4(color,trans),smoothstep(.005,.045,abs(ray.y)));
 result=current;
}

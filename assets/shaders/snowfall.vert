#version 400 core
layout(location=0) in vec3 aPosition;layout(location=2) in vec2 aUv;
uniform mat4 uView,uProjection,uShelterMatrix,uShelterInverse;
uniform vec3 uViewPosition,uCameraRight,uCameraUp;
uniform sampler2D uShelter;
uniform vec2 uWindOffset,uDriftPhase;
uniform float uFallTime,uSnowfall,uGust;
out vec2 vUv;out vec3 vWorld;out float vAlpha;
#include "precipitation.glsl"
void main(){
 vec3 seed=aPosition;float variant=fract(seed.x*71.3+seed.y*37.1);
 float speed=1.0+floor(variant*4.0)*.25;
 vec3 p=seed*vec3(48,32,48);p.xz+=uWindOffset;p.y-=uFallTime*speed;
 p.xz+=floor((uViewPosition.xz-p.xz)/48.0+.5)*48.0;
 p.y+=floor((uViewPosition.y+8.0-p.y)/32.0+.5)*32.0;
 p.xz+=sin(seed.xz*31.0+uDriftPhase)*.7;
 vec4 shelter=uShelterMatrix*vec4(p,1);vec2 uv=shelter.xy/shelter.w*.5+.5;
 float roof=precipitationHeight(uv);
 float distanceToEye=distance(p,uViewPosition);
 float alpha=(1.0-smoothstep(16.0,24.0,length(p.xz-uViewPosition.xz)))*smoothstep(.25,1.0,distanceToEye);
 alpha*=1.0-smoothstep(10.0,16.0,abs(p.y-uViewPosition.y-8.0));
 alpha*=smoothstep(roof+.035,roof+.18,p.y);
 float densityRank=fract(seed.x*57.13+seed.z*91.77);
 alpha*=1.0-smoothstep(uSnowfall-.04,uSnowfall,densityRank);
 if(any(lessThan(uv,vec2(.01)))||any(greaterThan(uv,vec2(.99))))alpha=0.0;
 float size=mix(.016,.045,seed.z);
 // Only individual falling flakes use billboards. Ground spindrift lives in
 // the shared scattering volume, so it has depth and no camera-facing discs.
 vec3 offset=uCameraRight*aUv.x*size+uCameraUp*aUv.y*size*(1.0+uGust*.6);
 p+=offset;vWorld=p;vUv=aUv;vAlpha=alpha*.68;
 gl_Position=uProjection*uView*vec4(p,1);
}

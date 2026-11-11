uniform sampler2D uShelter;
uniform mat4 uShelterMatrix,uShelterInverse;
uniform float uBlowingSnowDensity,uSnowTravel;
#include "precipitation.glsl"
// Periodic lattice lets the integrated wind wrap without a visible phase jump.
float driftHash(vec3 p){return cloudHash(mod(p,128.0));}
float driftNoise(vec3 p){
 vec3 i=floor(p),f=fract(p);f=f*f*(3-2*f);
 return mix(mix(mix(driftHash(i),driftHash(i+vec3(1,0,0)),f.x),mix(driftHash(i+vec3(0,1,0)),driftHash(i+vec3(1,1,0)),f.x),f.y),
            mix(mix(driftHash(i+vec3(0,0,1)),driftHash(i+vec3(1,0,1)),f.x),mix(driftHash(i+vec3(0,1,1)),driftHash(i+1),f.x),f.y),f.z);
}
float blowingSnow(vec3 p){
 if(uBlowingSnowDensity<=0.0)return 0.0;
 vec4 shelter=uShelterMatrix*vec4(p,1);vec2 uv=shelter.xy/shelter.w*.5+.5;
 float border=min(min(uv.x,1-uv.x),min(uv.y,1-uv.y));
 if(border<=.02)return 0.0;
 float floorHeight=precipitationHeight(uv),height=p.y-floorHeight;
 if(height<=0.0||height>=2.8)return 0.0;
 vec2 wind=normalize(vec2(1,.38));
 vec3 q=vec3((dot(p.xz,wind)-uSnowTravel)*.125,height*.65,dot(p.xz,vec2(-wind.y,wind.x))*.375);
 float n=driftNoise(q)*.72+driftNoise(q*2.0+vec3(17,5,39))*.28;
 float wisps=smoothstep(.30,.76,n);
 return uBlowingSnowDensity*wisps*smoothstep(.02,.16,border)*smoothstep(0.0,.16,height)*(1.0-smoothstep(.35,2.8,height));
}

#ifndef DESERT_DUST
#define DESERT_DUST
#include "surface_ecology.glsl"
#include "mist_rooms.glsl"
uniform float uDustExtinction,uDustGust,uDustMoving;uniform vec2 uDustTravel,uDustWind;
vec3 dustLight(){return decodeColor(vec3(201,155,105)/255.0)*(ambientSkyColor()*.7+sunLightColor()*max(uSunDirection.y,0.0)*.65);}
float dustDensity(vec3 p){
 if(uDustExtinction<=0.0)return 0.0;
 vec4 data=surfaceData(p.xz);float ground=data.b> -4000?data.b:uGroundLevel,h=p.y-ground;
 float haze=uDustExtinction*exp(-max(h,0.0)/70.0),wisps=0;
 if(uDustMoving>0&&h>.025&&h<1.5&&data.b> -4000){
  vec2 q=p.xz-uDustTravel;float n=surfaceNoise(vec2(dot(q,vec2(.935,.355))/12.0,dot(q,vec2(-.355,.935))/3.0),uint(uSurfaceSeed)+713u);
  wisps=looseSand(p.xz,data)*smoothstep(.4,.75,n)*(1-smoothstep(.3,mix(.8,1.5,uDustGust),h))*smoothstep(.025,.12,h)*(.006+.008*uDustGust);
 }
 return (haze+wisps)*outsideMist(p);
}
float dustOpticalDepth(vec3 p,vec3 eye){
 if(uDustExtinction<=0)return 0;
 vec3 delta=p-eye;float distance=length(delta),density=0;
 for(int i=0;i<4;i++)density+=dustDensity(eye+delta*((float(i)+.5)/4.0));
 return density*.25*distance;
}
#endif

// Shared rounded snow relief, in metres; no geometry or collision displacement.
float snowHash(vec2 p){return fract(sin(dot(p,vec2(127.1,311.7)))*43758.5453);}
float snowNoise(vec2 p){vec2 i=floor(p),f=fract(p);f=f*f*(3.0-2.0*f);return mix(mix(snowHash(i),snowHash(i+vec2(1,0)),f.x),mix(snowHash(i+vec2(0,1)),snowHash(i+1.0),f.x),f.y);}
float snowHeight(vec2 p){return .020*snowNoise(p*2.4)+.007*snowNoise(p*7.1)+.003*snowNoise(p*19.0);}
float snowHitDistance=0.;
vec2 snowReliefUv(vec3 p,vec3 eye,vec3 g){
 vec3 view=normalize(eye-p);float fade=(1.0-smoothstep(10.0,24.0,length(eye-p)))*smoothstep(.25,.65,view.y)*smoothstep(.65,.95,g.y);
 snowHitDistance=max(0.,.030-snowHeight(p.xz))*fade/max(view.y,.3);
 return p.xz-view.xz*snowHitDistance;
}
vec3 snowDetailNormal(vec2 p,vec3 g,float distance){
 float e=.015;vec2 slope=vec2(snowHeight(p+vec2(e,0))-snowHeight(p-vec2(e,0)),snowHeight(p+vec2(0,e))-snowHeight(p-vec2(0,e)))/(2.0*e);
 float grainFade=1.0-smoothstep(.015,.06,max(length(dFdx(p)),length(dFdy(p))));
 slope+=vec2(snowNoise(p*130.0)-.5,snowNoise(p*130.0+19.0)-.5)*.10*grainFade;
 vec3 d=vec3(-slope.x,0,-slope.y);d-=g*dot(g,d);
 return normalize(g+d*(1.0-smoothstep(18.0,65.0,distance)));
}
vec3 snowAlbedo(sampler2D map,vec2 p){return mix(texture(map,p/3.0).rgb,vec3(.88,.91,.94),.40)*mix(.92,1.02,snowNoise(p*1.2));}

uniform vec2 uCloudWind;
uniform float uCloudCoverage,uCloudBase,uCloudTop;
float cloudHash(vec3 p){p=fract(p*.1031);p+=dot(p,p.yzx+33.33);return fract((p.x+p.y)*p.z);}
float cloudNoise(vec3 p){vec3 i=floor(p),f=fract(p);f=f*f*(3-2*f);return mix(mix(mix(cloudHash(i),cloudHash(i+vec3(1,0,0)),f.x),mix(cloudHash(i+vec3(0,1,0)),cloudHash(i+vec3(1,1,0)),f.x),f.y),mix(mix(cloudHash(i+vec3(0,0,1)),cloudHash(i+vec3(1,0,1)),f.x),mix(cloudHash(i+vec3(0,1,1)),cloudHash(i+1),f.x),f.y),f.z);}
float cloudFilteredNoise(vec3 p,float footprint){
 float detail=1.-smoothstep(.18,.65,footprint);
 return detail<=0.? .5:mix(.5,cloudNoise(p),detail);
}
// Keep every local layer inside the global raymarch/shadow bounds. The fields
// travel with the cloud bodies, so wind does not slide them through a fixed lid.
vec2 cloudLayer(vec3 p){
 vec2 q=(p.xz+uCloudWind)*.0018;
 float elevation=cloudNoise(vec3(q*.38,7.13));
 float billow=cloudNoise(vec3(q*.91,19.7));
 float depth=cloudNoise(vec3(q*.47,31.2));
 float span=max(uCloudTop-uCloudBase,1.);
 float base=uCloudBase+span*(.04+.23*elevation+.06*billow);
 float thickness=span*(.42+.22*depth);
 return vec2(base,base+thickness);
}
float cloudDensityFiltered(vec3 p,float sampleLength){
 vec2 layer=cloudLayer(p);
 float h=(p.y-layer.x)/(layer.y-layer.x);if(h<=0||h>=1)return 0;
 vec3 q=(p+vec3(uCloudWind.x,0,uCloudWind.y))*.0018;
 // Broad weather cells leave real blue-sky gaps. Separate the coverage field
 // from the rounded bodies and small edge erosion so clear clouds stay defined.
 float weather=cloudNoise(vec3(q.x*.72,0.37,q.z*.72));
 float coverage=smoothstep(1.0-uCloudCoverage+.03,1.0-uCloudCoverage+.18,weather);
 if(coverage<=0.0)return 0.0;
 // Warp the lobes before adding finer structure; straight value-noise cells
 // otherwise show rectangular, uniformly shaded edges in the sky atlas.
 float footprint=sampleLength*.0018;
 vec3 warp=(cloudFilteredNoise(q*1.31,footprint*1.31)-.5)*vec3(1.,.7,1.3);
 vec3 detail=q+warp*.65;
 float body=cloudFilteredNoise(detail*vec3(1.,1.65,1.),footprint*1.65)*.55+cloudFilteredNoise(detail*2.07,footprint*2.07)*.30+cloudFilteredNoise(detail*4.51,footprint*4.51)*.15;
 float profile=smoothstep(0.0,.18,h)*(1.0-smoothstep(.48,1.0,h));
 float shape=body+coverage*.14-h*.18;
 float erosion=(1.0-cloudFilteredNoise(detail*9.3,footprint*9.3))*.18;
 return coverage*profile*smoothstep(.40,.53,shape-erosion)*1.25;
}
float cloudDensity(vec3 p){return cloudDensityFiltered(p,0.);}

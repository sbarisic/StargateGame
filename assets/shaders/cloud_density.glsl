uniform vec2 uCloudWind;
uniform float uCloudCoverage,uCloudBase,uCloudTop;
float cloudHash(vec3 p){p=fract(p*.1031);p+=dot(p,p.yzx+33.33);return fract((p.x+p.y)*p.z);}
float cloudGradient(vec3 cell,vec3 delta){
 int h=int(cloudHash(cell)*16.);
 float a=h<8?delta.x:delta.y,b=h<4?delta.y:((h==12||h==14)?delta.x:delta.z);
 return ((h&1)==0?a:-a)+((h&2)==0?b:-b);
}
float cloudNoise(vec3 p){
 vec3 i=floor(p),d=fract(p),f=d*d*d*(d*(d*6.-15.)+10.);
 float n=mix(mix(mix(cloudGradient(i,d),cloudGradient(i+vec3(1,0,0),d-vec3(1,0,0)),f.x),
                 mix(cloudGradient(i+vec3(0,1,0),d-vec3(0,1,0)),cloudGradient(i+vec3(1,1,0),d-vec3(1,1,0)),f.x),f.y),
             mix(mix(cloudGradient(i+vec3(0,0,1),d-vec3(0,0,1)),cloudGradient(i+vec3(1,0,1),d-vec3(1,0,1)),f.x),
                 mix(cloudGradient(i+vec3(0,1,1),d-vec3(0,1,1)),cloudGradient(i+1.,d-1.),f.x),f.y),f.z);
 return clamp(.5+n*.5,0.,1.);
}
float cloudFilteredNoise(vec3 p,float footprint){
 float detail=1.-smoothstep(.18,.65,footprint);
 return detail<=0.? .5:mix(.5,cloudNoise(p),detail);
}
// Rotate successive scales so the gradient-noise lattice cannot form aligned
// square weather cells or vertical walls. The basis is orthonormal.
vec3 cloudRotate(vec3 p){return mat3(.00,.80,.60,-.80,.36,-.48,-.60,-.48,.64)*p;}
// Keep every local layer inside the global raymarch/shadow bounds. The fields
// travel with the cloud bodies, so wind does not slide them through a fixed lid.
vec2 cloudLayer(vec3 p){
 vec2 q=(p.xz+uCloudWind)*.0018;
 float elevation=cloudNoise(vec3(q*.38,7.13));
 float billow=cloudNoise(cloudRotate(vec3(q*.91,19.7)));
 float depth=cloudNoise(vec3(q*.47,31.2));
 float span=max(uCloudTop-uCloudBase,1.);
 float base=uCloudBase+span*(.04+.23*elevation+.06*billow);
 float thickness=span*(.24+.40*smoothstep(.25,.75,depth));
 return vec2(base,base+thickness);
}
float cloudDensityFiltered(vec3 p,float sampleLength){
 vec2 layer=cloudLayer(p);
 float h=(p.y-layer.x)/(layer.y-layer.x);if(h<=0||h>=1)return 0;
 vec3 q=(p+vec3(uCloudWind.x,0,uCloudWind.y))*.0024;
 // Kilometre-scale weather regions gather clouds into loose groups rather
 // than filling the sky with equally spaced, equally sized puffs.
 float region=cloudNoise(cloudRotate(vec3(q.x*.13,5.37,q.z*.13)));
 float weather=cloudNoise(cloudRotate(vec3(q.x*.55,0.37,q.z*.55)))+(region-.5)*.55;
 float coverage=smoothstep(1.0-uCloudCoverage-.12,1.0-uCloudCoverage+.22,weather);
 if(coverage<=0.0)return 0.0;
 // Warp the lobes before adding finer structure; straight value-noise cells
 // otherwise show rectangular, uniformly shaded edges in the sky atlas.
 float footprint=sampleLength*.0024;
 vec3 detail=cloudRotate(q);
 float warp=cloudFilteredNoise(detail*.83+vec3(13.1,7.7,3.4),footprint*.83)-.5;
 detail+=warp*vec3(.8,.4,.65);
 float broad=cloudFilteredNoise(detail*.62,footprint*.62);
 float lobes=cloudFilteredNoise(cloudRotate(detail)*2.13+vec3(5.2,1.3,9.7),footprint*2.13);
 float fine=cloudFilteredNoise(cloudRotate(detail+17.3)*4.57,footprint*4.57);
 // Blend fixed spatial scales instead of multiplying coordinates by a
 // varying frequency (which distorts their scale far from the origin).
 float large=smoothstep(.32,.68,region);
 float body=mix(lobes*.68+fine*.32,broad*.72+lobes*.22+fine*.06,large);
 // Coverage shifts a 3D isosurface instead of multiplying an extruded 2D
 // cutout. Soft bottoms and tapered tops form connected cumulus lobes.
 float profile=smoothstep(0.,.12,h)*(1.-smoothstep(.38,1.,h));
 float shape=body-(1.-profile)*.35;
 float threshold=mix(.68,.32,coverage);
 float edge=1.-smoothstep(threshold,threshold+.18,shape);
 float erosion=(1.-cloudFilteredNoise(cloudRotate(detail)*7.3,footprint*7.3))*.18*edge;
 float core=smoothstep(threshold+.03,threshold+.21,shape-erosion);
 float wisps=smoothstep(threshold-.05,threshold+.10,shape-erosion)*.16;
 return (core*.85+wisps)*profile*mix(.55,1.,coverage);
}
float cloudDensity(vec3 p){return cloudDensityFiltered(p,0.);}

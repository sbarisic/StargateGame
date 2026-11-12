// Stars live on a seeded 3D shell, not lat/long texels. Their round pixel
// footprints are evaluated after projection, without a seam or polar pinch.
uniform float uCelestialSeed;
uniform mat4 uStarViewProjection;
uniform vec2 uStarViewport;
float starHash(vec3 p){p=fract(p*.1031);p+=dot(p,p.yzx+33.33);return fract((p.x+p.y)*p.z);}
vec3 starField(vec3 ray){
 float angle=uCelestialPhase*6.2831853,c=cos(angle),s=sin(angle);
 vec3 d=vec3(c*ray.x-s*ray.z,ray.y,s*ray.x+c*ray.z);
 // Explicit projection remains valid in sky-only branches at silhouettes.
 // Derivatives there are undefined when adjacent lanes shade opaque surfaces.
 vec4 fragment=uStarViewProjection*vec4(ray,0.);
 if(fragment.w<=.0001)return vec3(0);
 vec3 cell=floor(d*96.),color=vec3(0);
 for(int z=-1;z<=1;z++)for(int y=-1;y<=1;y++)for(int x=-1;x<=1;x++){
  vec3 id=cell+vec3(x,y,z),key=id+uCelestialSeed;
  float h=starHash(key);if(h<.965)continue;
  vec3 position=id+vec3(starHash(key+17.),starHash(key+43.),starHash(key+79.));
  float radius=length(position);if(abs(radius-96.)>.5)continue;
  vec3 star=position/radius;
  vec3 worldStar=vec3(c*star.x+s*star.z,star.y,-s*star.x+c*star.z);
  vec4 projected=uStarViewProjection*vec4(worldStar,0.);
  if(projected.w<=.0001)continue;
  vec2 pixel=(projected.xy/projected.w-fragment.xy/fragment.w)*uStarViewport*.5;
  float brightness=.25+pow(starHash(key+103.),6.)*2.5;
  float width=mix(.65,1.05,starHash(key+137.));
  float footprint=exp(-2.*dot(pixel,pixel)/(width*width));
  color+=mix(vec3(.65,.8,1),vec3(1,.82,.6),starHash(key+163.))*brightness*footprint;
 }
 return color;
}

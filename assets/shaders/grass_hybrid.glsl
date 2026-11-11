// Shared by the baked near mesh and voxel representation.
uniform sampler2D uSurface;
uniform vec4 uTile;
uniform vec2 uTransition,uViewport;
uniform float uRange,uDensity,uGrowth,uGrassExtent,uGrassTip;
uniform int uGrassGroundLayer;
uniform mat4 uGrassViewProjection,uInverseViewProjection;
#include "meadow.glsl"
#include "terrain_pbr.glsl"
#include "grass_layout.glsl"
float grassHash(vec2 p){return fract(sin(dot(floor(p*32.),vec2(12.9898,78.233)))*43758.5453);}
vec4 grassSurface(vec2 p){return texture(uSurface,(clamp((p-uTile.xy)/8.,0.,1.)*32.+.5)/33.);}
vec3 grassGroundNormal(vec2 p){float dx=grassSurface(p+vec2(.25,0)).x-grassSurface(p-vec2(.25,0)).x;float dz=grassSurface(p+vec2(0,.25)).x-grassSurface(p-vec2(0,.25)).x;return normalize(vec3(-dx,.5,-dz));}
vec2 grassBend(vec2 root,float h){return uMeadowWind*meadowSway(root)*.24*h*h/(uGrassTip*max(.1,grassSurface(root).z));}
float grassTransition(vec2 p){return smoothstep(uTransition.x,uTransition.y,distance(p,uViewPosition.xz));}
bool grassKept(vec2 p){
 vec4 surface=grassSurface(p);
 ivec2 cell=ivec2(floor(clamp((p-uTile.xy)*4.,0.,31.)));
 // Hard exclusions remain empty throughout the interpolated terrain cell.
 float allowed=min(min(texelFetch(uSurface,cell,0).a,texelFetch(uSurface,cell+ivec2(1,0),0).a),min(texelFetch(uSurface,cell+ivec2(0,1),0).a,texelFetch(uSurface,cell+ivec2(1),0).a));
 float rangeFade=1.-smoothstep(uRange*.85,uRange,distance(p,uViewPosition.xz));
 return allowed>.5&&surface.y>.02&&grassHash(p+vec2(119.7,14.3))<surface.y*rangeFade*uGrowth*uDensity;
}
vec3 grassLight(vec3 world,vec3 normal,vec3 albedo,float stem,vec2 ambientVisibility,out vec3 ambient){
 vec3 groundNormal=grassGroundNormal(world.xz);
 vec3 n=normalize(vec3(normal.x,abs(normal.y)+.3,normal.z));
 float visibility=cloudVisibility(world)*SampleSunVisibility(world,groundNormal,max(dot(groundNormal,uSunDirection),0.));
 albedo=mix(vec3(dot(albedo,vec3(.2126,.7152,.0722))),albedo,.65)*vec3(1.03,1.,.85);
 // Match root pigmentation to the retained terrain material field.
 // Only root pigmentation is needed: do not evaluate three full PBR layers,
 // normals and height blending for every visible blade/volume hit.
 int layer=uGrassGroundLayer;
 float footprint=length(world-uViewPosition)*.001*float(textureSize(uTerrainColor,0).x)/uTerrainMetres[layer];
 vec3 ground=decodeColor(textureLod(uTerrainColor,vec3(world.xz/uTerrainMetres[layer],layer),log2(max(1.,footprint))).rgb)*uTerrainGain[layer];
 albedo=mix(albedo,ground,mix(.25,.08,smoothstep(0.,.65,stem)))*mix(.42,.95,stem);
 ambient=albedo*mix(ambientGroundColor()*mix(.65,1.,ambientVisibility.y),ambientSkyColor()*mix(.65,1.,ambientVisibility.x),.78);
 return ambient+albedo*sunLightColor()*(max(dot(n,uSunDirection),0.)+.20*max(dot(-normal,uSunDirection),0.))*visibility+gateLighting(world,normal,normalize(uViewPosition-world),albedo,.92);
}

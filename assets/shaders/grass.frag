#version 400 core
uniform vec3 uViewPosition;uniform mat4 uView;uniform vec2 uGrassTerrainBlend;
uniform sampler2D uTexture,uBladeNormal;
in vec3 vWorld,vNormal,vBladeSide;in float vStem;in vec2 vBladeUv,vBladeTexture;
flat in vec3 vRoot,vGroundNormal;flat in vec4 vGroundMaterial;
flat in float vShade,vLodStart,vLodEnd,vCoverage,vSeed;
layout(location=0) out vec4 fragColor;
#include "ao_outputs.glsl"

#include "daylight.glsl"
#include "shadows.glsl"
#include "gate_lighting.glsl"
#include "meadow.glsl"
#include "terrain_pbr.glsl"
void main(){
 vec2 cell=floor(vBladeUv*vec2(11,27));
 float dither=fract(sin(dot(cell,vec2(12.9898,78.233))+vSeed*211.)*43758.5453);
 if(dither<vLodStart||dither>=vLodEnd)discard;
 float coverageNoise=fract(sin(dot(cell,vec2(39.346,11.135))+vSeed*131.)*19341.345);
 if(coverageNoise>=vCoverage)discard;
 vec3 g=normalize(vNormal),n=gl_FrontFacing?g:-g;
 // Root colour matching needs the base material, not a camera-dependent
 // parallax march repeated on every blade fragment.
 TerrainSurface ground=terrainMaterial(vRoot,normalize(vGroundNormal),vGroundMaterial,10000.);
 vec3 grass=decodeColor(texture(uTexture,vBladeTexture).rgb)*vShade;
 vec3 scannedNormal=texture(uBladeNormal,vBladeTexture).rgb*2.-1.;
 vec3 tangent=normalize(vBladeSide-n*dot(n,vBladeSide));
 n=normalize(n+tangent*scannedNormal.x*.22+normalize(cross(n,tangent))*scannedNormal.y*.10);
 // Preserve photographed veins and subtle surface wear, while matching the local ground.
 grass*=mix(.78,1.08,vSeed);
 grass=mix(vec3(dot(grass,vec3(.2126,.7152,.0722))),grass,.65)*vec3(1.03,1.,.85);
 float blend=mix(uGrassTerrainBlend.x,uGrassTerrainBlend.y,smoothstep(0.,.65,vStem));
 vec3 base=mix(grass,ground.color,blend)*mix(.42,.95,vStem);
 base*=mix(.90,1.,1.-abs(vBladeUv.x))*(.96+.04*sin(vStem*19.+vSeed*13.));
 float visibility=cloudVisibility(vWorld)*SampleSunVisibility(vWorld,normalize(vGroundNormal),max(uSunDirection.y,0.));
 // Thin blades receive transmitted light on either face. Use an upward
 // foliage normal for diffuse collection so backlit blades are not black wires.
 vec3 lightNormal=normalize(vec3(n.x,abs(n.y)+.3,n.z));
 float diffuse=max(dot(lightNormal,uSunDirection),0.);
 vec3 ambient=base*mix(ambientGroundColor(),ambientSkyColor(),.78)*mix(.82,1.,vStem);
 vec3 light=ambient+base*(sunLightColor()*(diffuse+.20*max(dot(-n,uSunDirection),0.))*visibility);
 light+=gateLighting(vWorld,n,normalize(uViewPosition-vWorld),base,.92);
 writeAmbient(ambient,gl_FrontFacing?g:-g,3.);
 fragColor=vec4(applyFog(light,vWorld,uViewPosition),1);
}

#version 400 core
uniform vec3 uViewPosition;uniform mat4 uView;uniform sampler2D uTexture;
flat in vec2 vPatch;
flat in vec2 vBladeRoot;
in vec3 vWorld,vNormal,vRest;in vec2 vUv;in float vStem;
#include "gbuffer_outputs.glsl"
#include "daylight.glsl"
#include "grass_hybrid.glsl"
void main(){
 if(any(lessThan(vRest.xz,vPatch))||any(greaterThan(vRest.xz,vPatch+2.)))discard;
 // Density, growth and LOD select complete blades, never sections of a blade.
 // Keep per-fragment hard clipping at buildings and other exclusion boundaries.
 if(!grassAllowed(vRest.xz)||!grassKept(vBladeRoot)||grassHash(vBladeRoot)<grassTransition(vBladeRoot))discard;
 vec3 n=normalize(vNormal);if(!gl_FrontFacing)n=-n;
 vec3 color=grassAlbedo(vWorld,decodeColor(texture(uTexture,vUv).rgb),vStem);
 writeSurface(color,.92,n,0.,1.,n,.20,3.,vec3(0),1.,gl_FragCoord.z,gl_FragCoord.z);
}

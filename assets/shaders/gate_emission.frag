#version 400 core
in vec3 vWorld;
in vec2 vUv;
layout(location=0) out vec4 fragColor;

uniform sampler2D uTexture,uSceneDepth;
uniform vec3 uEmission,uViewPosition;
uniform vec2 uMaskSize;
uniform int uEmissiveOnly;
uniform vec4 uTravelPlane;
#include "daylight.glsl"
void main(){
 if(dot(vec4(vWorld,1),uTravelPlane)<0.)discard;
 if(uEmissiveOnly!=0&&gl_FragCoord.z>texture(uSceneDepth,gl_FragCoord.xy/uMaskSize).r+.000002)discard;
 float transmission=fogTransmission(vWorld,uViewPosition);
 // Linear RGB emission supports the orb's red lowlights and pale hot patches.
 // Existing glyph/chevron masks are greyscale, so their tint is unchanged.
 fragColor=vec4(texture(uTexture,vUv).rgb*uEmission*transmission,1);
}

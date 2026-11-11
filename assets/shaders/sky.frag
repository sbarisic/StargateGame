#version 400 core
in vec2 vUv;layout(location=0) out vec4 fragColor;
#include "ao_outputs.glsl"
uniform mat4 uInverseViewProjection;uniform vec3 uViewPosition;
#include "daylight.glsl"
void main(){
 excludeAmbient();vec4 f=uInverseViewProjection*vec4(vUv*2-1,1,1);vec3 ray=normalize(f.xyz/f.w-uViewPosition);
 vec3 color=skyColor(ray);
 // The atmospheric LUT already integrates clear air to space. Applying another
 // 6.5 km of generic fog to the entire sky erased its blue and cloud contrast.
 float localHaze=max(uOvercast,1.0-smoothstep(.01,.10,ray.y));
 if(localHaze>0.0)color=mix(color,applyFog(color,uViewPosition+ray*6500,uViewPosition),localHaze);
 fragColor=vec4(color,1);
}

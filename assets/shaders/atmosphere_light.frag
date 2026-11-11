#version 400 core
#include "atmosphere_parameters.glsl"
#include "ambient_tint.glsl"
uniform sampler2D uTrans,uIrr;uniform vec3 uSunDirection,uTwilightFill;uniform float uSkyIrradianceScale;out vec4 result;
void main(){vec3 sky;vec3 sun=GetSunAndSkyIrradiance(ATMOSPHERE,uTrans,uIrr,vec3(0,6360.002,0),vec3(0,1,0),uSunDirection,sky);
 result=vec4(gl_FragCoord.x<1.0?sun/max(uSunDirection.y,.00001):tintIndirect(max(vec3(.07,.085,.12),sky/3.14159265*uSkyIrradianceScale+uTwilightFill)),1);}

#version 400 core
#include "atmosphere_parameters.glsl"
in vec2 vUv;out vec4 result;
uniform sampler2D uTrans,uRay,uMie,uStars;
uniform vec3 uSunDirection;
uniform float uPhase,uAltitude,uSkyRadianceScale;
void main(){
 float az=(vUv.x-.5)*6.2831853,el=(vUv.y-.5)*3.14159265;
 vec3 ray=vec3(cos(el)*cos(az),sin(el),cos(el)*sin(az));
 ray.y=max(ray.y,.001);ray=normalize(ray);
 vec3 trans;vec3 col=GetSkyRadiance(ATMOSPHERE,uTrans,uRay,uMie,vec3(0,6360.0+max(.002,uAltitude*.001),0),ray,0.0,uSunDirection,trans)*uSkyRadianceScale;
 // The solar disc is evaluated at screen resolution in daylight.glsl. At
 // 512x256 its diameter is smaller than one texel of this scattering atlas.

 result=vec4(max(col,vec3(0)),1);
}

#version 400 core
in vec3 vWorld;in vec2 vUv;in float vAlpha;out vec4 fragColor;
uniform sampler2D uSceneDepth;uniform mat4 uInverseViewProjection;uniform vec2 uTargetSize;uniform vec3 uViewPosition;
#include "daylight.glsl"
void main(){
 float alpha=vAlpha*(1-smoothstep(.1,1.0,dot(vUv,vUv)));if(alpha<.002)discard;
 vec2 uv=gl_FragCoord.xy/uTargetSize;float depth=texture(uSceneDepth,uv).r;
 vec4 point=uInverseViewProjection*vec4(uv*2-1,depth*2-1,1);vec3 scene=point.xyz/point.w;
 alpha*=clamp((distance(scene,uViewPosition)-distance(vWorld,uViewPosition))/.25,0,1)*fogTransmission(vWorld,uViewPosition);
 fragColor=vec4(dustLight(),alpha);
}

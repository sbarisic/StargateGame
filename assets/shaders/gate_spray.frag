#version 400 core
in vec3 vWorld;in vec2 vUv;in float vAlpha;out vec4 fragColor;
uniform sampler2D uAtlas,uDetail,uSnow,uSceneDepth;
uniform mat4 uInverseViewProjection;
uniform vec2 uTargetSize;
uniform vec3 uViewPosition;
uniform int uEmissiveOnly;
uniform float uKawooshBloom;
#include "daylight.glsl"
void main(){
 // Static atlas cell zero, padded away from neighbouring frames.
 float density=texture(uAtlas,(vec2(.5)+vUv*127.0)/1024.0).a;
 float detail=texture(uDetail,vUv).r,speckle=texture(uSnow,vUv).r;
 // Soft radial falloff hides quad/atlas boundaries. Keep noisy breakup weak
 // so billboards read as mist rather than hard flakes.
 float feather=1.0-smoothstep(.25,1.0,length(vUv*2.0-1.0));
 float alpha=vAlpha*density*feather*(.85+.15*detail)*(.95+.05*speckle);
 vec2 uv=gl_FragCoord.xy/uTargetSize;float depth=texture(uSceneDepth,uv).r;
 if(gl_FragCoord.z>depth+.000002)discard;
 vec4 point=uInverseViewProjection*vec4(uv*2.0-1.0,depth*2.0-1.0,1);
 vec3 scene=point.xyz/point.w;
 alpha*=clamp((distance(scene,uViewPosition)-distance(vWorld,uViewPosition))/.18,0.0,1.0);
 alpha*=fogTransmission(vWorld,uViewPosition);
 if(alpha<.001)discard;
 vec3 color=mix(vec3(.12,.24,.38),vec3(.75,.90,1.05),detail);
 if(uEmissiveOnly!=0)color*=mix(.16,1.2,uKawooshBloom);
 fragColor=vec4(color*alpha,alpha);
}

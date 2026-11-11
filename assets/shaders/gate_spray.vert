#version 400 core
layout(location=0) in vec3 aPosition;
layout(location=2) in vec2 aUv;
uniform mat4 uModel,uView,uProjection;
uniform vec3 uCameraRight,uCameraUp;
uniform float uParticleAge,uParticleFade,uSeed,uGateScale,uBirthCutoff;
uniform int uParticleBudget;
uniform vec4 uSprayTimes;
out vec3 vWorld;
out vec2 vUv;
out float vAlpha;
#include "gate_transition.glsl"
float random(float n){return fract(sin(n*127.1+uSeed*311.7)*43758.5453);}
void main(){
 float id=aPosition.x,birth=mix(uSprayTimes.x,uSprayTimes.y,random(id+1.0));
 float life=mix(uSprayTimes.z,uSprayTimes.w,random(id+3.0)),age=uParticleAge-birth;
 float phase=clamp(age/life,0.0,1.0),angle=random(id+5.0)*6.2831853;
 // Bias half the budget towards the upper rolling head and its trailing folds.
 if(mod(id,2.0)<1.0)angle=mix(.30,2.84,random(id+5.0));
 vec2 radial=vec2(cos(angle),sin(angle));
 vec3 root=transitionPosition(radial*mix(.50,1.65,random(id+7.0)));
 root.xy+=radial*phase*.35;root.z+=phase*.35;root.y+=3.063692+phase*.12-phase*phase*.12;
 float size=mix(.12,.28,random(id+9.0))*(.65+phase)*uGateScale;
 vWorld=(uModel*vec4(root,1)).xyz+(uCameraRight*aUv.x+uCameraUp*aUv.y)*size;
 vUv=aUv*.5+.5;
 vAlpha=sin(phase*3.14159265)*uParticleFade*.32;
 if(age<0.0||age>=life||birth>uBirthCutoff||id>=float(uParticleBudget))vAlpha=0.0;
 gl_Position=vAlpha<.001?vec4(2,2,2,1):uProjection*uView*vec4(vWorld,1);
}

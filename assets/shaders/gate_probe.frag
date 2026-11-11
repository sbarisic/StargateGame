#version 400 core
in vec2 vUv;
out vec4 fragColor;
uniform int uProbeMode;
uniform vec2 uProbePoint;
uniform vec3 uExpected;
#include "gate_transition.glsl"
void main(){
 if(uProbeMode==1){
  bool hole=vUv.x>.30&&vUv.x<.55&&vUv.y>.40&&vUv.y<.65;
  fragColor=vec4(1,1,1,vUv.x<.75&&!hole?1:0);return;
 }
 vec3 error=abs(transitionPosition(uProbePoint)-uExpected);
 fragColor=vec4(clamp(error*100.0,0.0,1.0),1);
}

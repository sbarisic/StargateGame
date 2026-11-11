#version 400 core
layout(location=0) in vec3 aPosition;layout(location=2) in vec2 aUv;
uniform mat4 uView,uProjection;uniform vec3 uViewPosition,uCameraRight,uCameraUp;uniform float uDustRange,uDustTime;
out vec3 vWorld;out vec2 vUv;out float vAlpha;
#include "daylight.glsl"
void main(){
 vec2 centre=floor(uViewPosition.xz/8.0)*8.0;
 vec2 p=centre+mod(aPosition.xy*(uDustRange*2.0)+uDustTravel-centre+uDustRange,uDustRange*2.0)-uDustRange;
 vec4 field=surfaceData(p);float phase=fract(aPosition.z+uDustTime*.17),height=.05+.75*aPosition.z+.7*uDustGust*pow(aPosition.z,8.0);
 vec3 root=vec3(p.x,field.b+height,p.y);
 vAlpha=looseSand(p,field)*sin(phase*3.14159265)*(.12+.2*uDustGust)*(1-smoothstep(uDustRange*.7,uDustRange,distance(p,uViewPosition.xz)))*outsideMist(root)*uDustMoving;
 float width=.10+.22*aPosition.z;vec3 horizontal=normalize(vec3(uDustWind.x,0,uDustWind.y));
 vWorld=root+horizontal*aUv.x*width+uCameraUp*aUv.y*width*.22;vUv=aUv;
 gl_Position=field.b< -4000||vAlpha<.001?vec4(2,2,2,1):uProjection*uView*vec4(vWorld,1);
}

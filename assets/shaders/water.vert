#version 400 core
layout(location=0) in vec3 aPosition;
layout(location=2) in vec2 aFlow;
// FishGfx's normal attribute carries water depth, reconstructed from the bed.
layout(location=3) in vec3 aWaterData;
uniform mat4 uModel,uView,uProjection;
out vec3 vWorld;out vec2 vUv;out float vWaterDepth;
void main(){
 vec4 world=uModel*vec4(aPosition,1);
 vWorld=world.xyz;vUv=aFlow;vWaterDepth=aWaterData.x;
 gl_Position=uProjection*uView*world;
}

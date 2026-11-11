#version 400 core
#include "skinning.glsl"
layout(location=0) in vec3 aPosition;
layout(location=2) in vec2 aUv;
out vec2 vUv;
out vec3 vTravelWorld;
uniform mat4 uModel,uView,uProjection;
uniform int uTreeVertices;
uniform mat4 uTreeTransforms[8];
void main(){vUv=aUv;mat4 model=uTreeVertices<0?uTreeTransforms[gl_InstanceID]:uTreeVertices>0?uTreeTransforms[gl_VertexID/uTreeVertices]:uModel;vTravelWorld=(model*skinTransform()*vec4(aPosition,1)).xyz;gl_Position=uProjection*uView*vec4(vTravelWorld,1);}

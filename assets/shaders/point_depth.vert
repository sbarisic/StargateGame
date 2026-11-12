#version 400 core
#include "skinning.glsl"
layout(location=0) in vec3 aPosition;
layout(location=2) in vec2 aUv;
out vec2 vUv;
out vec3 vTravelWorld;
uniform mat4 uModel,uView,uProjection;
uniform int uTreeVertices;
uniform mat4 uTreeTransforms[8];
uniform vec4 uTile;
uniform int uTreeMode;
uniform float uTreeHeight,uTreeTime,uTreeWindStrength;
uniform vec2 uTreeWind;
#include "tree_wind.glsl"
void main(){
 vUv=aUv;
 mat4 model=uTreeVertices<0?uTreeTransforms[gl_InstanceID]:uTreeVertices>0?uTreeTransforms[gl_VertexID/uTreeVertices]:uModel;
 model=model*skinTransform();
 vTravelWorld=(model*vec4(treeWindPosition(aPosition,model),1)).xyz;
 vec4 clip=uProjection*uView*vec4(vTravelWorld,1);
 clip.xy=clip.xy*uTile.xy+clip.w*uTile.zw;
 gl_Position=clip;
}

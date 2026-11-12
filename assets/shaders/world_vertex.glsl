#include "skinning.glsl"
layout(location=0) in vec3 aPosition;
layout(location=1) in vec4 aColor;
layout(location=2) in vec2 aUv;
layout(location=3) in vec3 aNormal;
uniform mat4 uModel, uView, uProjection;
uniform int uTreeVertices;
uniform mat4 uTreeTransforms[8];
uniform int uTreeMode;
uniform float uTreeHeight,uTreeTime,uTreeWindStrength;
uniform vec2 uTreeWind;
out vec3 vTreeLocal;
flat out vec2 vTreeRange;uniform vec2 uTreeLodRange;uniform mat4 uTreeParameters[8];
SURFACE_SAMPLE out vec3 vWorld, vNormal;
SURFACE_SAMPLE out vec2 vUv;
out float vDistance;
out vec4 vMaterial;
#include "tree_wind.glsl"
void main() {
    mat4 model=uTreeVertices<0?uTreeTransforms[gl_InstanceID]:uTreeVertices>0?uTreeTransforms[gl_VertexID/uTreeVertices]:uModel;
    model=model*skinTransform();
    vTreeRange=uTreeVertices<0?uTreeParameters[gl_InstanceID][0].xy:uTreeLodRange;
    vec3 position=aPosition;vTreeLocal=position;
    position=treeWindPosition(position,model);
    vec4 world = model * vec4(position, 1.0);
    vec4 view = uView * world;
    vWorld = world.xyz;
    vNormal = normalize(transpose(inverse(mat3(model))) * aNormal);
    vUv = aUv;
    vMaterial = aColor;
    vDistance = length(view.xyz);
    gl_Position = uProjection * view;
}

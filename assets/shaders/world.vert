#version 400 core
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
out float vTreeAo;
flat out vec2 vTreeRange;uniform vec2 uTreeLodRange;uniform mat4 uTreeParameters[8];
out vec3 vWorld, vNormal;
out vec2 vUv;
out float vDistance;
out vec4 vMaterial;
void main() {
    mat4 model=uTreeVertices<0?uTreeTransforms[gl_InstanceID]:uTreeVertices>0?uTreeTransforms[gl_VertexID/uTreeVertices]:uModel;
    model=model*skinTransform();
    vTreeRange=uTreeVertices<0?uTreeParameters[gl_InstanceID][0].xy:uTreeLodRange;
    vec3 position=aPosition;vTreeLocal=position;vTreeAo=1.;
    if(uTreeMode>0){
        float stem=clamp(position.y/max(uTreeHeight,1.),0.,1.);
        vec3 root=(model*vec4(0,0,0,1)).xyz;
        float gust=sin(dot(root.xz,vec2(.62,.37))-uTreeTime*1.8)+.35*sin(dot(root.xz,vec2(1.9,-1.3))-uTreeTime*3.1);
        vec2 wind=(inverse(mat3(model))*vec3(uTreeWind.x,0,uTreeWind.y)).xz;
        position.xz+=wind*gust*uTreeWindStrength*stem*stem;
        if(uTreeMode==2){float fine=sin(dot(aPosition,vec3(8.1,5.7,3.4))+uTreeTime*3.3);position.xz+=wind*fine*.025*stem*(1.-smoothstep(25.,90.,length((uView*model*vec4(aPosition,1)).xyz)));vTreeAo=mix(.74,1.,stem);}
    }
    vec4 world = model * vec4(position, 1.0);
    vec4 view = uView * world;
    vWorld = world.xyz;
    vNormal = normalize(transpose(inverse(mat3(model))) * aNormal);
    vUv = aUv;
    vMaterial = aColor;
    vDistance = length(view.xyz);
    gl_Position = uProjection * view;
}

#version 400 core
layout(location=0) in vec3 aPosition;
uniform mat4 uModel,uView,uProjection;
uniform vec3 uGateCentre;
uniform float uGateScale;
out vec3 vWorld,vGatePosition;
out vec2 vMembrane;
out vec3 vSurfaceNormal,vSurfaceTangent,vSurfaceBitangent;
#include "gate_transition.glsl"
void main(){
 vec2 p=aPosition.xy-vec2(0,3.063692);
 vec3 local=transitionPosition(p);
 vec3 tangent=vec3(1,0,0),bitangent=vec3(0,1,0);
 if(surgeActivity()>0.0||uClosingAge>=0.0){
  const float e=.006;
  tangent=(transitionPosition(p+vec2(e,0))-transitionPosition(p-vec2(e,0)))/(2.0*e);
  bitangent=(transitionPosition(p+vec2(0,e))-transitionPosition(p-vec2(0,e)))/(2.0*e);
 }
 vSurfaceNormal=normalize(mat3(uModel)*cross(tangent,bitangent));
 vSurfaceTangent=normalize(mat3(uModel)*tangent);
 vSurfaceBitangent=normalize(cross(vSurfaceNormal,vSurfaceTangent));
 vGatePosition=local*uGateScale;
 local.y+=3.063692;
 vec4 world=uModel*vec4(local,1);
 vWorld=world.xyz;
 // Material coordinates stay fixed in gate metres through opening and closing.
 vMembrane=p*uGateScale;
 gl_Position=uProjection*uView*world;
}

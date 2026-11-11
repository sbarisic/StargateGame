#version 400 core
layout(location=0) in vec3 aPosition;layout(location=3) in vec3 aNormal;layout(location=2) in vec2 aUv;
uniform mat4 uView,uProjection;uniform vec3 uViewPosition;uniform sampler2D uRecords;
uniform int uVertices,uCount,uDetailed,uGrass;uniform float uRange,uBend,uDensity;
out vec3 vWorld,vNormal;flat out vec3 vRoot,vGroundNormal;flat out vec4 vGroundMaterial;out vec2 vUv;out float vFade,vShade,vStem;
#include "meadow.glsl"
vec4 recordAt(int i){return texelFetch(uRecords,ivec2(i%128,i/128),0);}
void main(){
 int i=gl_InstanceID;vec4 root=recordAt(i*4),variation=recordAt(i*4+1);float d=distance(root.xz,uViewPosition.xz);vRoot=root.xyz;vGroundMaterial=recordAt(i*4+2);vGroundNormal=recordAt(i*4+3).xyz;
 float lod=smoothstep(13.0,19.0,d);vFade=(uDetailed!=0?1-lod:lod)*(1-smoothstep(uRange-8,uRange,d));
 // Stable per-instance ranks retain a nested subset as the camera moves.
 // Concentrate coverage around the player instead of building a distant wall.
 float density=uDensity;
 if(uGrass!=0){
   float retention=mix(1.,.20,smoothstep(6.,16.,d));
   retention=mix(retention,.025,smoothstep(16.,32.,d));
   retention=mix(retention,.005,smoothstep(32.,48.,d));
   density*=retention;
 }
 vFade*=smoothstep(-.012,.012,density-variation.y);
 mat2 turn=mat2(cos(variation.x),-sin(variation.x),sin(variation.x),cos(variation.x));vec3 p=aPosition*root.w;p.xz=turn*p.xz;
 p.xz+=uMeadowWind*meadowSway(root.xz+vec2(variation.w))*pow(clamp(aPosition.y,0,1),2)*root.w*uBend;
 vWorld=root.xyz+p;vNormal=aNormal;vNormal.xz=turn*vNormal.xz;vUv=aUv;vStem=aPosition.y;vShade=variation.z;
 gl_Position=i>=uCount||vFade<=0?vec4(2,2,2,1):uProjection*uView*vec4(vWorld,1);
}

#version 400 core
layout(location=0) in vec3 aPosition;
uniform mat4 uView,uProjection;
uniform vec3 uViewPosition;
uniform sampler2D uRecords;
uniform int uLod;
uniform float uRange,uDensity,uGrowth,uDetailRange,uMiddleRange;uniform vec2 uHeightRange;
out vec3 vWorld,vNormal,vBladeSide;out float vStem;out vec2 vBladeUv,vBladeTexture;
flat out vec3 vRoot,vGroundNormal;flat out vec4 vGroundMaterial;
flat out float vShade,vLodStart,vLodEnd,vCoverage,vSeed;
#include "meadow.glsl"
vec4 recordAt(int i){return texelFetch(uRecords,ivec2(i%128,i/128),0);}
void main(){
 int vertices=uLod==2?3:uLod==0?21:9,localBlade=gl_VertexID/vertices;
 int blade=uLod==2?localBlade*4:localBlade%8;
 bool undergrowth=uLod==0&&localBlade>=8;
 vec4 rootHeight=recordAt(gl_InstanceID*6),variation=recordAt(gl_InstanceID*6+1);
 vRoot=rootHeight.xyz;vGroundMaterial=recordAt(gl_InstanceID*6+4);vGroundNormal=recordAt(gl_InstanceID*6+5).xyz;
 float angle=variation.x+float(blade)*2.39996;
 vec3 side=vec3(cos(angle),0,sin(angle)),facing=vec3(-side.z,0,side.x);
 vec3 root=vRoot+side*variation.w*(.24+.05*float(blade%3));
 root.y=recordAt(gl_InstanceID*6+2+blade/4)[blade%4];
 if(undergrowth){float a=angle+1.1;side=vec3(cos(a),0,sin(a));facing=vec3(-side.z,0,side.x);}
 float d=distance(vRoot.xz,uViewPosition.xz),fraction=d/uRange;
 float nearBlend=smoothstep(uDetailRange-3.,uDetailRange+3.,d),farBlend=smoothstep(uMiddleRange-4.,uMiddleRange+4.,d);
 vLodStart=uLod==0?0.:uLod==1?1.-nearBlend:1.-farBlend;
 vLodEnd=uLod==0?1.-nearBlend:uLod==1?1.-farBlend:1.;
 vCoverage=(1.-smoothstep(.84,1.,fraction))*uGrowth;
 float density=uDensity*mix(1.,.20,smoothstep(8.,30.,d))*mix(1.,.5,smoothstep(40.,uRange,d));
 vCoverage*=1.-smoothstep(density-.025,density+.025,variation.y);
 const vec2 fine[9]=vec2[9](vec2(-1,0),vec2(-.62,.52),vec2(1,0),vec2(1,0),vec2(-.62,.52),vec2(.62,.52),vec2(-.62,.52),vec2(0,1),vec2(.62,.52));
 const vec2 coarse[3]=vec2[3](vec2(-1,0),vec2(0,1),vec2(1,0));
 // Four segments let the tip curl instead of forming a straight spear.
 const vec2 curved[21]=vec2[21](
  vec2(-.45,0),vec2(-1,.22),vec2(.45,0),vec2(.45,0),vec2(-1,.22),vec2(1,.22),
  vec2(-1,.22),vec2(-.8,.48),vec2(1,.22),vec2(1,.22),vec2(-.8,.48),vec2(.8,.48),
  vec2(-.8,.48),vec2(-.46,.76),vec2(.8,.48),vec2(.8,.48),vec2(-.46,.76),vec2(.46,.76),
  vec2(-.46,.76),vec2(0,1),vec2(.46,.76));
 vec2 uv=uLod==2?coarse[gl_VertexID%3]:uLod==0?curved[gl_VertexID%21]:fine[gl_VertexID%9];
 float r=fract(sin(variation.x*17.1+float(blade)*19.7)*4375.58);
 float height=clamp(rootHeight.w*(.55+.70*r),uHeightRange.x*.65,uHeightRange.y);
 if(undergrowth)height*=.48;
 float width=mix(.004,.009,r)*(uLod==0?1.:uLod==1?1.45:4.0);
 float bend=.35+.65*r;
 vec3 p=root+side*uv.x*width+vec3(0,height*(uv.y-.22*r*uv.y*uv.y*uv.y),0)+facing*height*bend*uv.y*uv.y;
 float sway=meadowSway(vRoot.xz);p.xz+=uMeadowWind*sway*height*.24*uv.y*uv.y;
 vec3 along=vec3(0,1.-.66*r*uv.y*uv.y,0)+facing*(2.*bend*uv.y);
 along.xz+=2.*uMeadowWind*sway*.24*uv.y;
 vWorld=p;vNormal=normalize(cross(along,side));
 vBladeSide=side;
 float variant=floor(fract(variation.y*127.+float(blade)*.618)*7.);
 vBladeTexture=vec2((variant+mix(.04,.96,uv.x<0.?0.:uv.x>0.?1.:.5))/8.,uv.y);
 vStem=uv.y;vShade=variation.z;vBladeUv=uv;vSeed=variation.y;
 gl_Position=root.y< -4000.||vCoverage<=0.||vLodEnd<=vLodStart?vec4(2,2,2,1):uProjection*uView*vec4(p,1);
}

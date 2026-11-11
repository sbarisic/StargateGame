uniform sampler2D uMistGround;
uniform mat4 uMistGroundMatrix,uMistGroundInverse;
uniform float uGroundMistDensity,uMistTravel;
uniform vec2 uMistHeights;
#include "surface_ecology.glsl"
uniform int uForestMist;

float woodlandMist(vec3 p){
 if(uForestMist==0)return 0.;
 vec4 env=surfaceData(p.xz);if(env.b< -4000.)return 0.;
 float h=p.y-env.b;
 float canopy=smoothstep(0.,.65,env.r);
 float patchDensity=mix(.65,1.,surfaceNoise((p.xz-vec2(uMistTravel,0))/24.,uint(uSurfaceSeed)+307u));
 return .006*canopy*patchDensity*smoothstep(0.,.15,h)*(1.-smoothstep(8.,20.,h));
}

// Bilinear height, with an explicit validity check on every source texel.
// Interpolating the clear depth into real ground would invent surfaces at map holes.
bool mistSurface(vec3 p,out float height){
 vec4 clip=uMistGroundMatrix*vec4(p,1);
 vec2 uv=clip.xy/clip.w*.5+.5,sz=vec2(textureSize(uMistGround,0));
 vec2 cell=uv*sz-.5,f=fract(cell);ivec2 i=ivec2(floor(cell));
 if(any(lessThan(i,ivec2(0)))||any(greaterThanEqual(i+1,ivec2(sz))))return false;
 float a=texelFetch(uMistGround,i,0).r,b=texelFetch(uMistGround,i+ivec2(1,0),0).r;
 float c=texelFetch(uMistGround,i+ivec2(0,1),0).r,d=texelFetch(uMistGround,i+ivec2(1,1),0).r;
 if(max(max(a,b),max(c,d))>=.99999)return false;
 float depth=mix(mix(a,b,f.x),mix(c,d,f.x),f.y);
 vec4 world=uMistGroundInverse*vec4(uv*2-1,depth*2-1,1);
 height=world.y/world.w;return true;
}

float groundMist(vec3 p,vec3 eye){
 if(uGroundMistDensity<=0.0)return 0;
 float range=length(p.xz-eye.xz);
 if(range>=120.0)return 0;
 float surface;if(!mistSurface(p,surface))return 0;
 float h=p.y-surface;
 if(h<=0.0||h>=uMistHeights.y)return 0;
 vec2 wind=normalize(vec2(1,.38));
 vec3 q=vec3((dot(p.xz,wind)-uMistTravel)/32.0,h*.5,dot(p.xz,vec2(-wind.y,wind.x))/16.0);
 float n=driftNoise(q)*.75+driftNoise(q*2.0+vec3(17,5,39))*.25;
 float blanket=1.0-smoothstep(uMistHeights.x*.55,uMistHeights.x,h);
 float wisps=smoothstep(.52,.72,n)*(1.0-smoothstep(uMistHeights.x*.65,uMistHeights.y,h));
 float profile=max(blanket,wisps*.65)*smoothstep(0.0,.08,h);
 float coverage=uForestMist!=0?smoothstep(0.,.65,surfaceData(p.xz).r):1.;
 float variation=uForestMist!=0?mix(.70,1.,n):mix(.85,1.2,n);
 return uGroundMistDensity*coverage*variation*profile*(1.0-smoothstep(96.0,120.0,range));
}

#ifndef SURFACE_ECOLOGY
#define SURFACE_ECOLOGY
uniform sampler2D uSurfaceField;
uniform int uSurfaceEnabled,uSurfaceSeed;
uint surfaceHash(uint x){x^=x>>16;x*=0x7feb352du;x^=x>>15;x*=0x846ca68bu;return x^(x>>16);}
float surfaceRandom(ivec2 p,uint salt){return float(surfaceHash(uint(p.x)*73856093u^uint(p.y)*19349663u^salt)&65535u)/65535.0;}
float surfaceNoise(vec2 p,uint salt){ivec2 i=ivec2(floor(p));vec2 f=smoothstep(0.0,1.0,fract(p));return mix(mix(surfaceRandom(i,salt),surfaceRandom(i+ivec2(1,0),salt),f.x),mix(surfaceRandom(i+ivec2(0,1),salt),surfaceRandom(i+ivec2(1,1),salt),f.x),f.y);}
float surfacePatch(vec2 p){return .7*surfaceNoise(p/18.0,uint(uSurfaceSeed))+.3*surfaceNoise(p/2.6,uint(uSurfaceSeed)+19u);}
vec4 surfaceData(vec2 p){return uSurfaceEnabled!=0?texture(uSurfaceField,(p+2048.0)/4096.0):vec4(0,0,-4096,0);}
float looseSand(vec2 p,vec4 data){return data.a*(1.0-smoothstep(.08,.48,data.g))*smoothstep(.25,.55,surfacePatch(p));}
uniform float uMeadowPatchStrength;
float meadowEcology(vec4 env,float patchValue){return env.a*mix(1.,.32,env.r)*clamp(.65+env.g*.55,0.,1.)*mix(1.,smoothstep(.22,.62,patchValue),uMeadowPatchStrength);}
#endif

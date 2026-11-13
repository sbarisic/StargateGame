uniform vec3 uSunDirection, uSunColor, uZenith, uHorizon, uAmbientSky, uAmbientGround;
#include "ambient_tint.glsl"
uniform float uGroundLevel,uSunDiscScale,uOvercast,uGust;
uniform float uSkyGroundBounce;
uniform vec3 uFog;
vec3 decodeColor(vec3 c) { return mix(c / 12.92, pow((c + .055) / 1.055, vec3(2.4)), step(vec3(.04045), c)); }
uniform sampler2D uAtmosphereSky,uAtmosphereLight,uCloudVolume,uCloudShadow,uFogVolume,uCelestial;
uniform float uCelestialPhase;
#include "stars.glsl"
uniform int uAtmosphereEnabled,uVolumeEnabled,uCloudEnabled,uFogDeferred;
uniform vec3 uVolumeGrid;uniform mat4 uVolumeViewProjection;
vec2 skyUv(vec3 ray){return vec2(atan(ray.z,ray.x)/6.2831853+.5,asin(clamp(ray.y,-1.0,1.0))/3.14159265+.5);}
vec3 sunLightColor(){return uAtmosphereEnabled==1?texture(uAtmosphereLight,vec2(.25,.5)).rgb*uSunColor:uSunColor;}
vec3 ambientSkyColor(){return uAtmosphereEnabled==1?texture(uAtmosphereLight,vec2(.75,.5)).rgb*uAmbientSky:tintIndirect(uAmbientSky);}
vec3 ambientGroundColor(){return max(uAmbientGround,ambientSkyColor()*uSkyGroundBounce);}
float cloudVisibility(vec3 p){return uCloudEnabled==1?texture(uCloudShadow,clamp(p.xz/4096+.5,vec2(0),vec2(1))).r:1.0;}
vec3 solarDisc(vec3 ray){
 // Deliberately enlarged to match the game's art direction (~3.7 degrees
 // across). This changes the visible disc, not scene-light energy or shadows.
 float distance=length(ray-uSunDirection),radius=.0325;
 float edge=max(fwidth(distance),.00005);
 float disc=1.-smoothstep(radius-edge*.5,radius+edge*.5,distance);
 float horizon=smoothstep(-.001,.001,ray.y);
 vec3 transmitted=texture(uAtmosphereLight,vec2(.25,.5)).rgb;
 // Match the enlarged disc with a continuous circumsolar aureole. The physical
 // scattering atlas still supplies the rest of the sky; these smooth lobes
 // extend its forward-scattering glow to the artistic solar size. Composite
 // before clouds so a covered sun cannot paint a halo over opaque cloud bodies.
 float lowSun=1.-smoothstep(.08,.55,uSunDirection.y);
 float width=mix(.045,.060,lowSun);
 float aureole=2.4*exp(-distance*distance/(width*width));
 aureole+=mix(.22,.40,lowSun)*exp(-distance*distance/(.18*.18));
 vec3 warmth=mix(vec3(1.,.92,.72),vec3(1.,.70,.32),lowSun);
 float daylight=smoothstep(-.015,.02,uSunDirection.y);
 return transmitted*(vec3(16.*disc)+warmth*aureole)*horizon*daylight*uSunDiscScale;
}
vec3 cloudSunRays(vec3 ray,float transmission){
 // Angular light shafts from the existing cloud transmittance atlas. Sampling
 // directions, rather than screen coordinates, keeps them stable with camera
 // rotation and also gives reflections the same sky. Opaque scene geometry
 // still occludes this sky contribution in the normal depth-tested pass.
 float angle=length(ray-uSunDirection);
 if(uCloudEnabled==0||uSunDirection.y<=0.||ray.y<=0.||angle>=.50||transmission<.02)return vec3(0);
 float sum=0.,weights=0.,low=1.,high=0.;
 // Dense, fixed quadrature avoids repeating cloud-edge bands along the rays.
 // There is no frame-varying jitter to shimmer during camera movement.
 for(int i=0;i<64;i++){
  float t=(float(i)+.5)/64.;
  vec3 direction=normalize(mix(ray,uSunDirection,t*.92));
  float gap=texture(uCloudVolume,skyUv(direction)).a;
  float weight=1.-t*.55;
  sum+=gap*weight;weights+=weight;low=min(low,gap);high=max(high,gap);
 }
 // A uniformly clear or uniformly overcast sky has no shafts. Clouds shape
 // the streaks; no random starburst is painted onto the sun or opaque clouds.
 float contrast=smoothstep(.08,.65,high-low);
 float strength=.26*(sum/weights)*contrast*transmission;
 strength*=1.-smoothstep(.10,.50,angle);
 strength*=smoothstep(0.,.045,uSunDirection.y)*smoothstep(0.,.03,ray.y)*uSunDiscScale;
 return texture(uAtmosphereLight,vec2(.25,.5)).rgb*strength;
}
vec3 skyColor(vec3 ray) {
 if(uAtmosphereEnabled==1){vec2 uv=skyUv(ray);vec3 c=texture(uAtmosphereSky,uv).rgb;
 c+=solarDisc(ray);
 float night=1.0-smoothstep(-.16,.03,uSunDirection.y);
 if(night>0. && ray.y>-.02){
  vec3 celestial=texture(uCelestial,vec2(fract(uv.x+uCelestialPhase),uv.y)).rgb+starField(ray);
  c+=celestial*night*exp(-.025/max(ray.y,.008))*smoothstep(-.02,.05,ray.y);
 }
 if(uCloudEnabled==1){vec4 cloud=texture(uCloudVolume,skyUv(ray));c=c*cloud.a+cloud.rgb+cloudSunRays(ray,cloud.a);}return c;}
 return mix(uHorizon,uZenith,pow(max(ray.y,0),.42));
}
vec4 volumeAt(vec3 position,vec3 eye){
 float z=clamp(log(1+length(position-eye))/log(6501.0)*uVolumeGrid.z-1,0,uVolumeGrid.z-1);
 vec4 clip=uVolumeViewProjection*vec4(position,1);vec2 uv=clamp(clip.xy/max(clip.w,.0001)*.5+.5,.5/uVolumeGrid.xy,1.0-.5/uVolumeGrid.xy);
 float lo=floor(z),hi=min(lo+1,uVolumeGrid.z-1);vec2 dims=vec2(8,ceil(uVolumeGrid.z/8));
 vec4 a=texture(uFogVolume,(uv+vec2(mod(lo,8),floor(lo/8)))/dims),b=texture(uFogVolume,(uv+vec2(mod(hi,8),floor(hi/8)))/dims);
 return mix(a,b,fract(z));
}
#include "desert_dust.glsl"
float fogTransmission(vec3 position, vec3 eye) {
 if(uVolumeEnabled==1)return volumeAt(position,eye).a;
 return exp(-length(position-eye)*(uFog.x+uFog.y*exp(-max((position.y+eye.y)*.5-uGroundLevel,0)*uFog.z))-dustOpticalDepth(position,eye));
}
vec3 applyFog(vec3 color,vec3 position,vec3 eye){
 if(uFogDeferred==1)return color;
 if(uVolumeEnabled==1){vec4 v=volumeAt(position,eye);return color*v.a+v.rgb;}
 float air=exp(-length(position-eye)*(uFog.x+uFog.y*exp(-max((position.y+eye.y)*.5-uGroundLevel,0)*uFog.z)));
 return mix(dustLight(),mix(scatteringIndirect(tintIndirect(skyColor(normalize(position-eye))),uSunDirection.y),color,air),exp(-dustOpticalDepth(position,eye)));
}

uniform sampler2D uFarSunDepth;uniform mat4 uFarSunMatrix;uniform int uFarSunEnabled;
float farSunVisibility(vec3 p){
 if(uFarSunEnabled==0)return 1;vec4 c=uFarSunMatrix*vec4(p,1);vec3 uv=c.xyz/c.w*.5+.5;
 if(any(lessThan(uv,vec3(0)))||any(greaterThan(uv,vec3(1))))return 1;
 float sum=0;for(int y=-1;y<=1;y++)for(int x=-1;x<=1;x++)sum+=step(uv.z-.00035,texture(uFarSunDepth,uv.xy+vec2(x,y)/1024).r);return sum/9;
}

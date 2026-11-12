#include "gbuffer_common.glsl"
layout(location=0) out vec4 gAlbedo;
layout(location=1) out vec4 gShading;
layout(location=2) out vec4 gGeometry;
layout(location=3) out vec4 gEmission;
layout(location=4) out float gBaseDepth;
uniform mat4 uSurfaceViewProjection;
float projectedSurfaceDepth(vec3 p,float fallback){vec4 clip=uSurfaceViewProjection*vec4(p,1);float d=clip.z/clip.w*.5+.5;return (clip.w>0. && !isnan(d) && !isinf(d))?clamp(d,0.,1.):fallback;}
void writeSurface(vec3 base,float rough,vec3 n,float metal,float spec,vec3 g,float transmission,float kind,vec3 emission,float sunVisibility,float baseDepth,float depth){
 gAlbedo=vec4(max(base,vec3(0)),clamp(rough,.06,1.));
 gShading=vec4(octEncode(normalize(n)),clamp(metal,0.,1.),spec);
 gGeometry=vec4(octEncode(normalize(g)),transmission,kind);
 gEmission=vec4(max(emission,vec3(0)),clamp(sunVisibility,0.,1.));
 gBaseDepth=baseDepth;gl_FragDepth=depth;
}

#define IN(x) const in x
#define OUT(x) out x
#define TEMPLATE(x)
#define TEMPLATE_ARGUMENT(x)
#define assert(x)
const int TRANSMITTANCE_TEXTURE_WIDTH=256, TRANSMITTANCE_TEXTURE_HEIGHT=64;
const int IRRADIANCE_TEXTURE_WIDTH=64, IRRADIANCE_TEXTURE_HEIGHT=16;
const int SCATTERING_TEXTURE_R_SIZE=16, SCATTERING_TEXTURE_MU_SIZE=64, SCATTERING_TEXTURE_MU_S_SIZE=16, SCATTERING_TEXTURE_NU_SIZE=8;
#include "bruneton/definitions.glsl"
uniform float uAerosol;
AtmosphereParameters atmosphereModel(){
 return AtmosphereParameters(vec3(1.474,1.8504,1.91198),.004675,6360.0,6460.0,
 DensityProfile(DensityProfileLayer[2](DensityProfileLayer(0,0,0,0,0),DensityProfileLayer(0,1,-.125,0,0))),vec3(.005802,.013558,.033100),
 DensityProfile(DensityProfileLayer[2](DensityProfileLayer(0,0,0,0,0),DensityProfileLayer(0,1,-.833333,0,0))),vec3(.003996)*uAerosol,vec3(.00444)*uAerosol,.8,
 DensityProfile(DensityProfileLayer[2](DensityProfileLayer(25,0,0,.0666667,-.666667),DensityProfileLayer(0,0,0,-.0666667,2.666667))),
 vec3(.000650,.001881,.000085),vec3(.1),-.207912);
}
#define ATMOSPHERE atmosphereModel()
vec4 atmosphereTexture(sampler2D tex,vec2 uv){return texture(tex,uv);}
vec4 atmosphereTexture(sampler2D tex,vec3 uv){
 float z=clamp(uv.z*16.0-.5,0.0,15.0),a=floor(z),b=min(a+1.0,15.0);
 vec2 xy=clamp(uv.xy,vec2(.5/128.0,.5/64.0),vec2(1.0-.5/128.0,1.0-.5/64.0));
 return mix(texture(tex,vec2(xy.x,(xy.y+a)/16.0)),texture(tex,vec2(xy.x,(xy.y+b)/16.0)),fract(z));
}
#include "bruneton/functions.glsl"

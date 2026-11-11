uniform vec3 uWaterAbsorption,uWaterScattering;
uniform float uWaterIor,uWaterRoughness,uWaterReflectionStrength;
uniform int uWaterBiome;
// Use world position, not camera altitude or streamed terrain availability, so
// distant water and underwater views agree and travel cannot leave stale tints.
float waterBlueWeight(vec3 position){
 if(uWaterBiome!=0)return 0.0;
 // Match the temperate regional snow band. Icy worlds always use neutral water.
 float snow=smoothstep(430.0,650.0,position.y);
 float daylight=smoothstep(.02,.35,uSunDirection.y)*(1.0-clamp(uOvercast,0.0,1.0));
 float sunlight=mix(.35,1.0,smoothstep(.15,.85,cloudVisibility(position)));
 return (1.0-snow)*daylight*sunlight;
}
vec3 waterScatteringAt(vec3 position){
 return mix(uWaterScattering,vec3(.025,.075,.068),waterBlueWeight(position));
}
// Linear light, inverse metres. Shared above-water and underwater transport.
vec3 waterTransport(vec3 radiance,float distance,vec3 position){
 float blue=waterBlueWeight(position);
 vec3 absorption=mix(uWaterAbsorption,vec3(.30,.095,.055),blue);
 vec3 scattering=mix(uWaterScattering,vec3(.025,.075,.068),blue);
 vec3 transmission=exp(-absorption*max(distance,0.0));
 return radiance*transmission+scattering*max(.08,length(ambientSkyColor()))*(1.0-transmission);
}
float waterFresnel(float cosine){
 float f0=(uWaterIor-1.0)/(uWaterIor+1.0);f0*=f0;
 return f0+(1.0-f0)*pow(1.0-clamp(cosine,0.0,1.0),5.0);
}
// The volumetric screen buffer is integrated from the camera. It cannot be
// reused for reflected rays or the air portion of an underwater ray.
vec3 waterAirTransport(vec3 color,vec3 point,vec3 origin){
 vec3 delta=point-origin;float distance=length(delta);
 if(distance<.0001)return color;
 float air=exp(-distance*(uFog.x+uFog.y*exp(-max((point.y+origin.y)*.5-uGroundLevel,0.0)*uFog.z)));
 return mix(dustLight(),mix(skyColor(delta/distance),color,air),exp(-dustOpticalDepth(point,origin)));
}

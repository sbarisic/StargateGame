// Receiver-local reflected light. Enabled only while drawing this DHD's metal,
// never its orb, the terrain, other consoles, or actors. No extra shadow maps.
uniform int uDhdLightCount;
uniform vec4 uDhdLightPositionRange[38],uDhdLightNormalRadius[38];
uniform vec3 uDhdLightColor[38];
vec3 dhdLightingParts(vec3 p,vec3 geometricNormal,vec3 normal,vec3 view,vec3 albedo,float rough,float specularStrength,out vec3 specular){
 vec3 sum=vec3(0);specular=vec3(0);
 float top=smoothstep(.2,.5,geometricNormal.y);
 if(uDhdLightCount==0||top<=0.0)return sum;
 for(int i=0;i<uDhdLightCount;i++){
  vec3 delta=uDhdLightPositionRange[i].xyz-p;
  float distance=length(delta),range=uDhdLightPositionRange[i].w;
  if(distance>=range)continue;
  vec3 sourceNormal=uDhdLightNormalRadius[i].xyz;
  float radius=uDhdLightNormalRadius[i].w;
  // Restrict the approximation to nearby upper surfaces, not through the housing.
  float facing=smoothstep(.15,.65,dot(geometricNormal,sourceNormal));
  float plane=smoothstep(-2.0*radius,-radius,dot(-delta,sourceNormal));
  float softDistance=sqrt(dot(delta,delta)+radius*radius);
  vec3 toward=delta/max(distance,.00001);
  float diffuse=max(dot(normal,delta),0.0)/softDistance;
  float fade=1.0-distance*distance/(range*range);
  float attenuation=fade*fade/(softDistance*softDistance);
  float power=mix(96.0,6.0,rough*rough);
  vec3 halfVector=view+toward;
  halfVector/=max(length(halfVector),.00001);
  float spec=pow(max(dot(normal,halfVector),0.0),power)*.04*(power+2.0)/8.0;
  vec3 irradiance=uDhdLightColor[i]*diffuse*attenuation*top*facing*plane;
  sum+=albedo*irradiance;specular+=spec*specularStrength*irradiance;
 }
 return sum;
}

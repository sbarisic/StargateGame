// Local spray density injected into the existing fog transport. No extra volume
// target, history or fullscreen compositor; water and opaque depth share it.
uniform int uKawooshCount,uKawooshSamples;
uniform mat4 uKawooshInverse[2];
uniform vec4 uKawooshParameters[2]; // envelope, authoritative age, lagged extension, retraction
float mistGaussian(float x){return exp(-x*x);}
float kawooshMist(vec3 world){
 float density=0.0;
 for(int i=0;i<uKawooshCount;i++){
  vec4 uKawoosh=uKawooshParameters[i];
  vec3 p=(uKawooshInverse[i]*vec4(world,1)).xyz-vec3(0,3.063692,0);
  if(abs(p.x)>3.3||p.y< -3.2||p.y>3.9||p.z<-.35||p.z>7.1)continue;
  float age=uKawoosh.y,reach=uKawoosh.z,retract=uKawoosh.w;
  vec3 flow=vec3(.12*age,-.36*age,-.65*age);
  float coarse=cloudNoise(p*2.0+flow),detail=cloudNoise(p*5.1+flow*1.7);
  vec3 head=p-vec3(0,.18+max(age-2.2,0.0)*.25,3.72*reach);
  vec3 axes=vec3(1.75*(1-.10*retract),1.80,1.80*reach+.18);
  float shell=(length(head/axes)-1.0)*min(axes.x,axes.z);
  shell+=(coarse-.5)*.35;
  float headMist=mistGaussian(shell/.46);
  float neck=mistGaussian((length(p.xy)-1.13*(1-.35*retract))/.40);
  neck*=smoothstep(.0,.35,p.z)*(1-smoothstep(2.2*reach,3.1*reach+.3,p.z));
  float collar=mistGaussian((length(p.xy)-2.15)/.40)*mistGaussian(p.z/.30)*.35;
  float upper=mix(.65,1.25,smoothstep(-.6,1.3,p.y));
  float erosion=smoothstep(.20,.72,coarse*.7+detail*.3);
  float bounds=smoothstep(-.35,.10,p.z)*(1-smoothstep(6.4,7.1,p.z));
  density+=uKawoosh.x*.85*max(headMist,neck*.65)*upper*erosion*bounds;
  density+=uKawoosh.x*.15*collar*erosion;
 }
 return min(density,1.2);
}

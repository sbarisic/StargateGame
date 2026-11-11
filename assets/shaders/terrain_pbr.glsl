uniform vec2 uTerrainWind;
uniform float uRoadPaved;
#include "surface_ecology.glsl"
uniform sampler2DArray uTerrainColor,uTerrainNormal,uTerrainMasks;
uniform float uTerrainMetres[11],uTerrainGain[11],uTerrainNormalStrength[11],uTerrainHeightStrength[11];uniform int uTerrainBiome;
uniform float uTerrainGrass[11];
uniform float uTerrainRelief[11],uTerrainParallaxRange;
uniform int uTerrainParallaxSteps;
struct TerrainSurface {vec3 color;vec3 normal;float ao;float rough;float height;float grass;float shadow;};
float terrainTraceHeight(int id,vec2 uv,vec2 dx,vec2 dy,float cover){
 return textureGrad(uTerrainMasks,vec3(uv,id),dx,dy).b;
}
// Bounded parallax occlusion: white height is the top of a recessed slab.
// Explicit unwarped derivatives keep mip selection stable inside divergent marches.
vec2 terrainReliefUv(int id,vec2 uv,vec3 view,vec2 dx,vec2 dy,float strength,float cover){
 if(strength<.00001||uTerrainParallaxSteps==0)return uv;
 float facing=abs(view.z);strength*=smoothstep(.035,.16,facing);
 if(strength<.00001)return uv;
 vec2 ray=view.xy/max(facing,.15)*strength;
 int steps=int(mix(float(uTerrainParallaxSteps),10.,clamp(facing,0.,1.)));
 float stepDepth=1./float(steps),depth=0.;
 vec2 at=uv;
 float sampled=1.-terrainTraceHeight(id,at,dx,dy,cover);
 for(int i=0;i<48;i++){
  if(i>=steps||depth>=sampled)break;
  depth+=stepDepth;at-=ray*stepDepth;
  sampled=1.-terrainTraceHeight(id,at,dx,dy,cover);
 }
 if(depth==0.)return uv;
 // Refine the first intersection, rather than interpolating across a tall
 // discontinuity. This removes the staircase bands along raised stone edges.
 float low=depth-stepDepth,high=depth;
 for(int i=0;i<5;i++){
  float mid=(low+high)*.5;
  float surface=1.-terrainTraceHeight(id,uv-ray*mid,dx,dy,cover);
  if(mid<surface)low=mid;else high=mid;
 }
 return uv-ray*((low+high)*.5);
}
// Bounded height-field sunlight visibility; ambient remains unshadowed.
// Algorithm reference: LiveTrower, Godot Shaders (CC0), linked in BUILDING.md.
float terrainHeightShadow(int id,vec2 uv,vec3 light,vec2 dx,vec2 dy,float strength){
 if(strength<.00001||uTerrainParallaxSteps==0)return 1.;
 if(light.z<=0.)return 0.;
 float start=1.-terrainTraceHeight(id,uv,dx,dy,0.);
 int steps=int(mix(float(uTerrainParallaxSteps),8.,clamp(light.z,0.,1.)));
 vec2 ray=light.xy/max(light.z,.08)*strength;
 float visibility=1.;
 for(int i=1;i<=48;i++){
  if(i>steps)break;
  float rise=start*float(i)/float(steps);
  float surface=1.-terrainTraceHeight(id,uv+ray*rise,dx,dy,0.);
  visibility=min(visibility,1.-smoothstep(.01,.04,start-rise-surface));
  if(visibility<=0.)break;
 }
 return visibility;
}
TerrainSurface terrainLayer(int id,vec3 p,vec3 g,float distance){
 float metres=uTerrainMetres[id];vec3 q=p/metres;vec2 uv=q.xz;
 mat2 wind=mat2(uTerrainWind.x,-uTerrainWind.y,uTerrainWind.y,uTerrainWind.x);
 bool dunes=uTerrainBiome==1 && id<2; if(dunes)uv=wind*uv;
 float cover=0.;
 vec3 dx=dFdx(q),dy=dFdy(q),view=normalize(uViewPosition-p);
 float relief=uTerrainRelief[id]/metres*(1.-smoothstep(uTerrainParallaxRange*.35,uTerrainParallaxRange,distance));
 relief*=smoothstep(.02,.14,dot(g,view));
 uv=terrainReliefUv(id,uv,view.xzy,dx.xz,dy.xz,relief,cover);
 vec3 sunY=uSunDirection.xzy;
 if(dunes)sunY.xy=wind*sunY.xy;
 float selfShadow=terrainHeightShadow(id,uv,sunY,dx.xz,dy.xz,relief*smoothstep(.035,.16,abs(view.y)));
 vec3 projection=pow(abs(g),vec3(6));projection/=max(dot(projection,vec3(1)),.0001);
 projection=mix(vec3(0,1,0),projection,1-smoothstep(.65,.85,g.y));
 vec4 col=textureGrad(uTerrainColor,vec3(uv,id),dx.xz,dy.xz),m=textureGrad(uTerrainMasks,vec3(uv,id),dx.xz,dy.xz);
 // Normal detail has faded completely by 140 m; avoid those distant fetches.
 vec3 detail=vec3(0);if(distance<140.0)detail=textureGrad(uTerrainNormal,vec3(uv,id),dx.xz,dy.xz).xyz*2.0-1.0;detail.y=-detail.y;
 vec2 surfaceDetail=dunes?transpose(wind)*detail.xy:detail.xy;
 vec3 perturb=vec3(surfaceDetail.x,0,surfaceDetail.y);
 // Only slopes pay for three projections. Map perturbations into world space.
 if(g.y<.85){
  vec2 ux=terrainReliefUv(id,q.zy,view.zyx,dx.zy,dy.zy,relief,0.);
  vec2 uz=terrainReliefUv(id,q.xy,view.xyz,dx.xy,dy.xy,relief,0.);
  selfShadow=selfShadow*projection.y+
   terrainHeightShadow(id,ux,vec3(uSunDirection.zy,uSunDirection.x*sign(g.x)),dx.zy,dy.zy,relief*smoothstep(.035,.16,abs(view.x)))*projection.x+
   terrainHeightShadow(id,uz,vec3(uSunDirection.xy,uSunDirection.z*sign(g.z)),dx.xy,dy.xy,relief*smoothstep(.035,.16,abs(view.z)))*projection.z;
  col=col*projection.y+textureGrad(uTerrainColor,vec3(ux,id),dx.zy,dy.zy)*projection.x+textureGrad(uTerrainColor,vec3(uz,id),dx.xy,dy.xy)*projection.z;
  m=m*projection.y+textureGrad(uTerrainMasks,vec3(ux,id),dx.zy,dy.zy)*projection.x+textureGrad(uTerrainMasks,vec3(uz,id),dx.xy,dy.xy)*projection.z;
  vec3 x=vec3(0),z=vec3(0);if(distance<140.0){x=textureGrad(uTerrainNormal,vec3(ux,id),dx.zy,dy.zy).xyz*2-1;z=textureGrad(uTerrainNormal,vec3(uz,id),dx.xy,dy.xy).xyz*2-1;}
  perturb=perturb*projection.y+vec3(0,-x.y,x.x)*projection.x+vec3(z.x,-z.y,0)*projection.z;
 }
 perturb-=g*dot(perturb,g);
 TerrainSurface s;s.color=min(vec3(.9),decodeColor(col.rgb)*uTerrainGain[id]);s.normal=normalize(g+perturb*uTerrainNormalStrength[id]*(1-smoothstep(35.0,140.0,distance)));s.ao=m.r;s.rough=clamp(m.g,.15,1);s.height=m.b*uTerrainHeightStrength[id];s.grass=uTerrainGrass[id];s.shadow=selfShadow;return s;
}
TerrainSurface terrainMaterial(vec3 p,vec3 g,vec4 material,float distance,vec2 habitat){
 vec4 env=surfaceData(p.xz);float canopy=max(env.r,habitat.x),wet=env.b> -4000?max(env.g,material.b*.45):material.b*.45;
 float bank=smoothstep(.4,.85,wet);
 float patchValue=surfacePatch(p.xz),large=surfaceNoise(p.xz/43.0,uint(uSurfaceSeed)+51u);
 float cliff=smoothstep(.55,1.0,material.g),dry=smoothstep(.5,.78,patchValue),shade=smoothstep(.15,.8,canopy);
 float w[8];for(int i=0;i<8;i++)w[i]=0;int rock=7;
 if(uTerrainBiome==0){
  w[0]=(1-shade)*(1-dry)*(1-wet);w[1]=(1-shade)*(.25+large*.5)*(1-wet);
  w[0]+=w[1];w[1]=0.; // Both grass roles share one canonical material.
  w[2]=shade*(1-wet*.7);w[3]=shade*wet;w[4]=bank*(1-cliff)*(1-large);w[5]=bank*large;w[6]=dry*(1-shade)*(1-wet);
 }else if(uTerrainBiome==1){
  rock=6;float sand=smoothstep(.15,2.5,habitat.y)*(1-wet);
  w[0]=sand*(1-large);w[1]=sand*large;w[2]=(1-sand)*(1-wet)*(1-large);w[3]=(1-sand)*(1-wet)*large;w[4]=cliff*(1-cliff)*2;w[5]=cliff*.35;w[7]=bank;
 }else{
  rock=5;w[0]=(1-dry)*(1-shade*.5)*(1-wet*.5);w[1]=large*.6;w[2]=dry*.7;w[3]=cliff*(1-cliff)*2;w[4]=wet*.45*(1-shade);w[6]=shade*dry*.65;w[7]=wet*dry*.5;
 }
 int a=0,b=0;float first=-1,second=-1,third=0;
 for(int i=0;i<8;i++){if(i==rock)continue;float v=w[i];if(v>first){third=second;second=first;b=a;first=v;a=i;}else if(v>second){third=second;second=v;b=i;}else third=max(third,v);}
 float aw=max(.001,first-max(0,third)),bw=max(0,second-max(0,third));float total=aw+bw;
 TerrainSurface x=terrainLayer(a,p,g,distance),y=x,z;
 if(bw>.001&&b!=a)y=terrainLayer(b,p,g,distance);
 vec3 weights=vec3(aw/total*(1-cliff),bw/total*(1-cliff),cliff);
 if(cliff>.001)z=terrainLayer(rock,p,g,distance);else z=x;
 // Height only modifies existing coverage. The dominant base always survives.
 vec3 scores=weights+vec3(x.height,y.height,z.height);float peak=max(scores.x,max(scores.y,scores.z));
 vec3 blend=max(vec3(0),scores-peak+.5)*weights;float sum=dot(blend,vec3(1));weights=sum>1e-6?blend/sum:weights;
 TerrainSurface r;r.color=x.color*weights.x+y.color*weights.y+z.color*weights.z;
 r.shadow=dot(vec3(x.shadow,y.shadow,z.shadow),weights);
 r.grass=dot(vec3(x.grass,y.grass,z.grass),weights);
 r.normal=normalize(x.normal*weights.x+y.normal*weights.y+z.normal*weights.z);
 r.ao=mix(1.0,x.ao*weights.x+y.ao*weights.y+z.ao*weights.z,.65);
 r.rough=x.rough*weights.x+y.rough*weights.y+z.rough*weights.z;r.height=0;
 r.color*=mix(.91,1.07,large);r.color*=1-wet*.14;r.rough=mix(r.rough,max(.22,r.rough*.65),wet);
 float paved=uRoadPaved;float road=material.r;
 if(road>.01){
  // Reuse the biome's stone/soil material so roads retain ground detail.
  int pathLayer=paved>.5?8+int(mod(floor(p.z/22.),3.)):(uTerrainBiome==0?6:uTerrainBiome==1?2:5);
  TerrainSurface path=terrainLayer(pathLayer,p,g,distance);
  vec3 tint=mix(vec3(.8,.66,.48),vec3(1.),paved);
  r.color=mix(r.color,path.color*tint,road);
  r.shadow=mix(r.shadow,path.shadow,road);
  r.grass*=1.-smoothstep(0.,.04,road);
  r.normal=normalize(mix(r.normal,path.normal,road));r.rough=mix(r.rough,max(.85,path.rough),road);r.ao=mix(r.ao,path.ao,road);
 }
 if(uTerrainBiome==0 && habitat.x<0.){
  float beach=clamp(-habitat.x,0.,1.);TerrainSurface sand=terrainLayer(1,p,g,distance);
  float damp=(1.-smoothstep(.15,2.,p.y))*.35;
  r.color=mix(r.color,sand.color*(1.-damp),beach);r.normal=normalize(mix(r.normal,sand.normal,beach));r.rough=mix(r.rough,mix(.9,.5,damp),beach);
  r.grass=mix(r.grass,sand.grass,beach);r.shadow=mix(r.shadow,sand.shadow,beach);
 }
 if(uTerrainBiome==1){float layers=.94+.06*sin(p.y*2.3+sin(p.y*.41));r.color*=mix(1.,layers,cliff);}
 if(uTerrainBiome==0 && material.a>.01){float snow=material.a*smoothstep(.45,.85,g.y);r.color=mix(r.color,vec3(.65,.7,.75),snow);r.rough=mix(r.rough,.9,snow);r.grass*=1.-snow;}
 return r;
}
TerrainSurface terrainMaterial(vec3 p,vec3 g,vec4 material,float distance){return terrainMaterial(p,g,material,distance,vec2(0,1));}
vec3 terrainBrdf(vec3 base,vec3 n,vec3 view,vec3 sun,float rough){
 vec3 halfVector=view+sun,h=halfVector*inversesqrt(max(dot(halfVector,halfVector),1e-8));float nv=max(dot(n,view),.001),nl=max(dot(n,sun),0),nh=max(dot(n,h),0),vh=max(dot(view,h),0);
 float a=max(.06,rough*rough),a2=a*a,d=a2/(3.14159265*pow(nh*nh*(a2-1)+1,2));
 float k=pow(rough+1,2)/8;float geometry=nv/(nv*(1-k)+k)*nl/max(nl*(1-k)+k,.001);
 vec3 fresnel=vec3(.04)+vec3(.96)*pow(1-vh,5);
 return ((1-fresnel)*base/3.14159265+d*geometry*fresnel/max(4*nv*nl,.001))*nl*3.14159265;
}

float deferredAo(vec2 uv,float depth,vec4 g){
 if(uEnabled==0)return 1.;
 vec3 p=reconstruct(uv,depth),n=octDecode(g.xy);
 vec2 size=vec2(textureSize(uAo,0)),q=uv*size-.5,f=fract(q),origin=floor(q);
 float total=0.,sum=0.;
 for(int y=0;y<2;y++)for(int x=0;x<2;x++){
  vec2 at=(origin+vec2(x,y)+.5)/size;vec4 ng=texture(uGuideNormal,at);float d=texture(uGuideDepth,at).r;
  if(d>=.99999||abs(ng.a-g.a)>.1)continue;
  float w=(x==0?1.-f.x:f.x)*(y==0?1.-f.y:f.y);
  float separation=abs(dot(reconstruct(at,d)-p,n));if(separation>uAoRadius*.25)continue;
  w*=exp(-separation*32./max(uAoRadius,.1))*pow(max(dot(n,octDecode(ng.xy)),0.),8.);
  total+=w;sum+=texture(uAo,at).r*w;
 }
 return total>.0001?sum/total:1.;
}

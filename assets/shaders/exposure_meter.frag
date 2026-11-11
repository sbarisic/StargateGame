#version 400 core
in vec2 vUv;out vec4 result;
uniform sampler2D uTexture,uDepth;
void main(){
 vec2 size=vec2(160,90);vec2 sum=vec2(0);
 // Four stratified samples retain small foreground surfaces during reduction.
 for(int y=0;y<2;y++)for(int x=0;x<2;x++){
  vec2 uv=vUv+(vec2(x,y)-.5)*.5/size;
  vec3 rgb=textureLod(uTexture,uv,0).rgb;
  float l=dot(max(rgb,vec3(0)),vec3(.2126,.7152,.0722));
  if(isnan(l)||isinf(l))continue;
  float w=mix(.25,1.,exp(-4.*dot(uv-.5,uv-.5)));
  if(textureLod(uDepth,uv,0).r>=.999999)w*=.25;
  // Bound the contribution of dark voids, the sun and emissive highlights.
  sum+=vec2(clamp(log2(max(l,1e-6)),-10.,4.)*w,w);
 }
 result=vec4(sum,0,1);
}

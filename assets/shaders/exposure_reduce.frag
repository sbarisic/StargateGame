#version 400 core
out vec4 result;uniform sampler2D uTexture;
void main(){
 ivec2 base=ivec2(gl_FragCoord.xy)*4,size=textureSize(uTexture,0);vec2 sum=vec2(0);
 for(int y=0;y<4;y++)for(int x=0;x<4;x++){
  ivec2 p=base+ivec2(x,y);if(all(lessThan(p,size)))sum+=texelFetch(uTexture,p,0).rg;
 }
 // Sum weights explicitly: odd reduction dimensions must not bias the result.
 result=vec4(sum,0,1);
}

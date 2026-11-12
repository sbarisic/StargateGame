#version 400 core
in vec2 vUv; out vec4 result;
#include "ao_common.glsl"
uniform sampler2D uAo;
uniform float uRadius;
void main(){
    float depth=texture(uDepth,vUv).r;
    vec4 normal=texture(uNormals,vUv);
    if(depth>=.99999||materialClass(normal)<.5){result=vec4(1);return;}
    vec3 position=surfacePosition(vUv,depth), n=octDecode(normal.xy);
    vec2 pixel=1./vec2(textureSize(uAo,0));
    float sum=0.,total=0.;
    for(int y=-2;y<=2;y++)for(int x=-2;x<=2;x++){
        vec2 uv=vUv+vec2(x,y)*pixel;
        float d=texture(uDepth,uv).r;
        vec4 other=texture(uNormals,uv);
        if(d>=.99999||abs(other.a-normal.a)>.1)continue;
        vec3 delta=surfacePosition(uv,d)-position;
        float separation=abs(dot(delta,n));
        if(separation>uRadius*.25)continue;
        float weight=exp(-float(x*x+y*y)*.3-separation*24./max(uRadius,.1));
        weight*=pow(max(dot(n,octDecode(other.xy)),0.),8.);
        sum+=texture(uAo,uv).r*weight;total+=weight;
    }
    result=vec4(total>.0001?sum/total:1.);
}

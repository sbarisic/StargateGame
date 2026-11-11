#version 400 core
in vec2 vUv; out vec4 result;
#include "ao_common.glsl"
uniform sampler2D uColor,uAmbient,uAo,uRawAo;
uniform float uStrength;
uniform int uEnabled,uInspection;
void main(){
    vec4 color=texture(uColor,vUv), normal=texture(uNormals,vUv);
    float depth=texture(uDepth,vUv).r;
    vec3 ambient=texture(uAmbient,vUv).rgb;
    float visibility=1.;
    if(uEnabled!=0&&depth<.99999&&materialClass(normal)>.5){
        vec3 position=surfacePosition(vUv,depth),n=normalize(normal.xyz*2.-1.);
        vec2 size=vec2(textureSize(uAo,0)), p=vUv*size-.5, base=floor(p), f=fract(p);
        float sum=0.,total=0.;
        for(int y=0;y<2;y++)for(int x=0;x<2;x++){
            vec2 uv=(base+vec2(x,y)+.5)/size;
            vec4 other=texture(uNormals,uv);float d=texture(uDepth,uv).r;
            if(d>=.99999||abs(other.a-normal.a)>.1)continue;
            float weight=(x==0?1.-f.x:f.x)*(y==0?1.-f.y:f.y);
            weight*=exp(-abs(dot(surfacePosition(uv,d)-position,n))*32.);
            weight*=pow(max(dot(n,normalize(other.xyz*2.-1.)),0.),8.);
            sum+=texture(uAo,uv).r*weight;total+=weight;
        }
        if(total>.0001)visibility=sum/total;
    }
    float amount=clamp((1.-visibility)*uStrength*receiverWeight(materialClass(normal)),0.,1.);
    result=vec4(max(vec3(0),color.rgb-ambient*amount),color.a);
    if(uInspection==1)result=vec4(vec3(texture(uRawAo,vUv).r),1);
    if(uInspection==2)result=vec4(vec3(visibility),1);
    if(uInspection==3)result=vec4(normal.rgb,1);
    if(uInspection==4)result=vec4(ambient,1);
}

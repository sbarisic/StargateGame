#version 400 core
in vec2 vUv;out vec4 fragColor;uniform sampler2D uTexture;
float luma(vec3 c){return dot(c,vec3(.299,.587,.114));}
void main(){
 vec2 px=1.0/vec2(textureSize(uTexture,0));
 vec3 m=texture(uTexture,vUv).rgb;
 vec3 nw=texture(uTexture,vUv+vec2(-1,1)*px).rgb,ne=texture(uTexture,vUv+vec2(1,1)*px).rgb;
 vec3 sw=texture(uTexture,vUv+vec2(-1,-1)*px).rgb,se=texture(uTexture,vUv+vec2(1,-1)*px).rgb;
 float lm=luma(m),a=luma(nw),b=luma(ne),c=luma(sw),d=luma(se);
 float lo=min(lm,min(min(a,b),min(c,d))),hi=max(lm,max(max(a,b),max(c,d)));
 if(hi-lo<max(.035,hi*.125)){fragColor=vec4(m,1);return;}
 vec2 dir=vec2(-((a+b)-(c+d)),(a+c)-(b+d));
 float reduce=max((a+b+c+d)*.03125,.0078125);
 dir=clamp(dir/(min(abs(dir.x),abs(dir.y))+reduce),vec2(-4),vec2(4))*px;
 vec3 first=.5*(texture(uTexture,vUv+dir*(-1.0/6.0)).rgb+texture(uTexture,vUv+dir*(1.0/6.0)).rgb);
 vec3 second=first*.5+.25*(texture(uTexture,vUv-dir*.5).rgb+texture(uTexture,vUv+dir*.5).rgb);
 float result=luma(second);fragColor=vec4(result<lo||result>hi?first:second,1);
}

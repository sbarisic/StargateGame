#version 400 core
in vec2 vUv;layout(location=0) out vec4 result;layout(location=1) out float cloudDepth;
uniform sampler2D uCurrent,uCurrentDepth,uHistory,uHistoryDepth;uniform vec3 uEye,uPreviousEye;uniform vec2 uWindDelta;uniform float uHistoryWeight;
vec2 spherical(vec3 d){return vec2(atan(d.z,d.x)/6.2831853+.5,asin(clamp(d.y,-1,1))/3.14159265+.5);}
void main(){
 vec4 current=texture(uCurrent,vUv);cloudDepth=texture(uCurrentDepth,vUv).r;
 if(uHistoryWeight<=0){result=current;return;}
 float a=(vUv.x-.5)*6.2831853,e=(vUv.y-.5)*3.14159265;vec3 ray=vec3(cos(e)*cos(a),sin(e),cos(e)*sin(a));
 vec3 previousPoint=uEye+ray*cloudDepth+vec3(uWindDelta.x,0,uWindDelta.y)-uPreviousEye;vec2 oldUv=spherical(normalize(previousPoint));
 float previousDepth=texture(uHistoryDepth,oldUv).r,weight=uHistoryWeight;
 if(abs(previousDepth-length(previousPoint))>max(150.0,cloudDepth*.04))weight=0;
 vec4 low=current,high=current;vec2 texel=1.0/vec2(textureSize(uCurrent,0));
 for(int y=-1;y<=1;y++)for(int x=-1;x<=1;x++){vec4 tap=texture(uCurrent,vUv+vec2(x,y)*texel);low=min(low,tap);high=max(high,tap);}
 result=mix(current,clamp(texture(uHistory,oldUv),low,high),weight);
}

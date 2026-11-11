#version 400 core
out vec4 result;uniform sampler2D uDensity;uniform vec3 uGrid;
float dist(float s){return pow(6501.0,s/uGrid.z)-1;}
vec2 address(vec2 cell,float z){return (cell+vec2(mod(z,8),floor(z/8))*uGrid.xy)/(uGrid.xy*vec2(8,ceil(uGrid.z/8)));}
void main(){vec2 cell=mod(gl_FragCoord.xy,uGrid.xy);float layer=floor(gl_FragCoord.x/uGrid.x)+floor(gl_FragCoord.y/uGrid.y)*8;
 vec3 sum=vec3(0);float trans=1;for(int z=0;z<96;z++){if(float(z)>layer||float(z)>=uGrid.z)break;vec4 d=texture(uDensity,address(cell,float(z)));float t=exp(-d.a*(dist(float(z)+1)-dist(float(z))));sum+=trans*d.rgb/max(d.a,.000001)*(1-t);trans*=t;}
 result=vec4(sum,trans);}

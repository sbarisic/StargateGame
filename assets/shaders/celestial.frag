#version 400 core
in vec2 vUv;out vec4 result;uniform float uSeed;
float hash(vec3 p){p=fract(p*.1031);p+=dot(p,p.yzx+33.33);return fract((p.x+p.y)*p.z);}
float noise(vec3 p){vec3 i=floor(p),f=fract(p);f=f*f*(3-2*f);return mix(mix(mix(hash(i),hash(i+vec3(1,0,0)),f.x),mix(hash(i+vec3(0,1,0)),hash(i+vec3(1,1,0)),f.x),f.y),mix(mix(hash(i+vec3(0,0,1)),hash(i+vec3(1,0,1)),f.x),mix(hash(i+vec3(0,1,1)),hash(i+1),f.x),f.y),f.z);}
void main(){
 float a=(vUv.x-.5)*6.2831853,e=(vUv.y-.5)*3.14159265;vec3 d=vec3(cos(e)*cos(a),sin(e),cos(e)*sin(a));
 vec3 gal=normalize(vec3(.24,.76,.6));float band=exp(-pow(dot(d,gal)/.13,2));
 float dust=noise(d*17+uSeed)*.65+noise(d*39-uSeed)*.35;
 vec3 color=vec3(.009,.012,.022)+band*mix(vec3(.045,.045,.07),vec3(.08,.062,.08),noise(d*8))*smoothstep(.25,.72,dust);
 vec2 cell=floor(vUv*vec2(2048,1024));float h=hash(vec3(cell,uSeed));
 float star=step(.9982,h)*(.25+pow(hash(vec3(cell.yx,uSeed+3)),6)*2.5)*pow(cos(e),.4);
 color+=mix(vec3(.65,.8,1),vec3(1,.82,.6),hash(vec3(cell,uSeed+7)))*star;
 result=vec4(color,1);
}

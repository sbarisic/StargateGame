uniform sampler2D uMeadowMask;
uniform int uMeadowEnabled;
uniform float uMeadowRange;
uniform float uMeadowTime;
uniform vec2 uMeadowWind;
float meadowSway(vec2 p){return sin(dot(p,vec2(.62,.37))-uMeadowTime*1.8)+.35*sin(dot(p,vec2(1.9,-1.3))-uMeadowTime*3.1);}
vec3 meadowTint(vec2 p){float variation=.5+.25*sin(p.x*.19+sin(p.y*.16))+.25*sin(p.y*.32-p.x*.11);return mix(vec3(.57,.74,.43),vec3(.76,.90,.58),variation);}
float meadowCoverage(vec3 p){return uMeadowEnabled==0?0.0:texture(uMeadowMask,(p.xz+2048.0)/4096.0).r;}

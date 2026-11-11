// Shape controls are sampled once from GateEffectTimeline for all passes.
uniform float uOpeningAge,uClosingAge,uPlumeLength;
uniform vec4 uGateShape; // extension, retraction, activity, stability
uniform vec4 uGateSurface; // formation, flash, breakup, closing fade
uniform int uBackSheet;
float surgeAmount(){return uGateShape.x;}
float surgeActivity(){return uGateShape.z;}
float liquidNoise(vec3 p){
 return (sin(dot(p,vec3(1.7,2.3,1.1)))+sin(dot(p,vec3(-2.6,1.4,1.9)))+1.8)*.22
     +sin(dot(p,vec3(4.1,-3.3,2.7)))*.12;
}
// Isotropic value noise with an analytic gradient, used only by the surge.
float plumeHash(vec3 p){p=fract(p*.1031);p+=dot(p,p.yzx+33.33);return fract((p.x+p.y)*p.z);}
vec4 plumeNoise(vec3 p){
 vec3 i=floor(p),f=fract(p),u=f*f*f*(f*(f*6.0-15.0)+10.0),d=30.0*f*f*(f*(f-2.0)+1.0);
 float a=plumeHash(i),b=plumeHash(i+vec3(1,0,0)),c=plumeHash(i+vec3(0,1,0)),e=plumeHash(i+vec3(1,1,0));
 float g=plumeHash(i+vec3(0,0,1)),h=plumeHash(i+vec3(1,0,1)),j=plumeHash(i+vec3(0,1,1)),k=plumeHash(i+vec3(1,1,1));
 float z0=mix(mix(a,b,u.x),mix(c,e,u.x),u.y),z1=mix(mix(g,h,u.x),mix(j,k,u.x),u.y);
 vec3 gradient=vec3(mix(mix(b-a,e-c,u.y),mix(h-g,k-j,u.y),u.z),
                   mix(mix(c-a,e-b,u.x),mix(j-g,k-h,u.x),u.z),z1-z0)*d;
 return vec4(mix(z0,z1,u.z)-.5,gradient);
}
vec2 liquidBezier(vec2 a,vec2 b,vec2 c,vec2 d,float t){
 float v=1.0-t;return v*v*v*a+3.0*v*v*t*b+3.0*v*t*t*c+t*t*t*d;
}
vec3 transitionPosition(vec2 p){
 if(uBackSheet!=0)return vec3(p,0);
 float r=clamp(length(p)/2.30,0.0,1.0),s=surgeAmount(),retract=uGateShape.y;
 if(s<=0.0)return vec3(p,0);
 vec2 dir=p/max(length(p),.0001),profile;
 float bulgeRadial=1.0,bulgeAxial=.15;
 // A rounded leading volume, trailing stem and short aperture collar. The
 // meridian doubles back radially behind the egg; it is not an extruded disc.
 if(r<.68){
  float angle=(r/.68)*2.45;
  bulgeRadial=sin(angle);bulgeAxial=cos(angle);
  profile=vec2(1.67*sin(angle)*(1.0-.08*cos(angle)),3.10+1.50*cos(angle));
  profile.x*=1.0-.10*retract;
  profile.y-=retract*.9*exp(-pow(r/.23,2.0));
 }else if(r<.90){
  profile=liquidBezier(vec2(1.132,1.945),vec2(1.12,1.70),vec2(1.04,.75),vec2(1.06,.40),(r-.68)/.22);
  profile.x*=1.0-retract*mix(.10,.50,smoothstep(.68,.86,r));
 }else{
  float stem=1.0-.50*retract;
  profile=liquidBezier(vec2(1.06*stem,.40),vec2(1.12*stem,.16),vec2(1.55,.02),vec2(2.30,0),(r-.90)/.10);
 }
 vec3 surface=vec3(dir*profile.x,profile.y);
 float rim=1.0-smoothstep(.94,1.0,r);
 float bubbles=plumeNoise(surface*2.2+vec3(.2,-.6,-.8)*uOpeningAge).x;
 float rolling=plumeNoise(surface*3.6+vec3(.4,-.6,-1.1)*uOpeningAge).x;
 float folds=plumeNoise(surface*8.0+vec3(-.7,.3,1.2)*uOpeningAge).x;
 float shape=smoothstep(0.0,.35,s);
 float bulges=(.65*bubbles+.18*rolling+.045*folds)*rim*shape;
 float radius=mix(length(p),profile.x,shape)+bulges*bulgeRadial;
 vec3 result=vec3(dir*max(radius,0.0),s*(profile.y*(uPlumeLength/4.60)+bulges*bulgeAxial));
 result.xy+=s*rim*smoothstep(0.0,.15,r)*vec2(.025*sin(uOpeningAge*7.0),.035*sin(uOpeningAge*5.0));
 return result;
}

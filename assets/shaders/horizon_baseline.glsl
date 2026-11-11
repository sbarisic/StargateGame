vec2 randomGradient(vec2 cell,float seed){
 // Stable spatial hash: no repeating texture tile or frame-to-frame random seed.
 vec3 p=fract(vec3(cell.x,cell.y,cell.x)*vec3(.1031,.1030,.0973)+seed);
 p+=dot(p,p.yzx+33.33);
 vec2 g=fract((p.xx+p.yz)*p.zy)*2.0-1.0;
 return g*inversesqrt(max(dot(g,g),.0001));
}
vec3 noiseHeightGradient(vec2 p,float seed){
 vec2 cell=floor(p),f=fract(p);
 vec2 a=randomGradient(cell,seed),b=randomGradient(cell+vec2(1,0),seed);
 vec2 c=randomGradient(cell+vec2(0,1),seed),d=randomGradient(cell+vec2(1,1),seed);
 float va=dot(a,f),vb=dot(b,f-vec2(1,0)),vc=dot(c,f-vec2(0,1)),vd=dot(d,f-vec2(1));
 vec2 u=f*f*f*(f*(f*6.0-15.0)+10.0),du=30.0*f*f*(f*(f-2.0)+1.0);
 vec2 gradient=mix(mix(a,b,u.x),mix(c,d,u.x),u.y);
 gradient.x+=du.x*mix(vb-va,vd-vc,u.y);
 gradient.y+=du.y*(mix(vc,vd,u.x)-mix(va,vb,u.x));
 return vec3(mix(mix(va,vb,u.x),mix(vc,vd,u.x),u.y),gradient);
}
// X is height in metres; YZ is its analytic spatial gradient.
vec3 baseline(vec2 p,float t,float footprint){
 vec2 a=vec2(dot(p,vec2(1.7,.8))+t*.27,dot(p,vec2(-.9,1.5))-t*.21);
 vec2 q=p+sin(a)*uDistortion;
 mat2 jac=mat2(1.0+cos(a.x)*uDistortion*1.7,cos(a.y)*uDistortion*(-.9),
               cos(a.x)*uDistortion*.8,1.0+cos(a.y)*uDistortion*1.5);
 mat2 rotation=mat2(.8,.6,-.6,.8);
 float k=4.2*uWaveScale;
 vec3 n=noiseHeightGradient(q*k+vec2(t*.22,t*.10),.17);
 float f=.24*(1.0-smoothstep(.12,.45,k*footprint));
 float h=n.x*f/k;vec2 g=n.yz*f;
 k=5.4*uWaveScale;n=noiseHeightGradient(rotation*q*k+vec2(-t*.17,t*.13),.73);
 f=.16*(1.0-smoothstep(.12,.45,k*footprint));h+=n.x*f/k;g+=transpose(rotation)*n.yz*f;
 k=9.3*uWaveScale;n=noiseHeightGradient(q*k+vec2(t*.07,-t*.19),.39);
 f=.035*(1.0-smoothstep(.12,.45,k*footprint));h+=n.x*f/k;g+=n.yz*f;
 h+=.012*(sin(p.x*2.8+t*.6)/2.8-cos(p.y*3.4-t*.5)/3.4);
 g=transpose(jac)*g+vec2(cos(p.x*2.8+t*.6),sin(p.y*3.4-t*.5))*.012;
 return vec3(h,g);
}

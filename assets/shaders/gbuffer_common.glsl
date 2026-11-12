vec2 octEncode(vec3 n){n/=max(abs(n.x)+abs(n.y)+abs(n.z),1e-8);return n.z>=0.?n.xy:(1.-abs(n.yx))*mix(vec2(-1),vec2(1),step(vec2(0),n.xy));}
vec3 octDecode(vec2 e){vec3 n=vec3(e,1.-abs(e.x)-abs(e.y));float t=clamp(-n.z,0.,1.);n.xy+=mix(vec2(t),vec2(-t),step(vec2(0),n.xy));return normalize(n);}
// IDs: background 0, solid 1, leaf 2, grass 3, terrain 4, canopy 5,
// rifle 6, skin 7, snow 8, diagnostic emission 9, console receivers >= 10.
float aoClass(float id){return id==5.?2.:id>=6.?1.:id;}

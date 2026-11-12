uniform vec3 uAmbientTint=vec3(1);
vec3 tintIndirect(vec3 color){
 vec3 tinted=color*uAmbientTint;
 const vec3 luma=vec3(.2126,.7152,.0722);
 return tinted*dot(color,luma)/max(dot(tinted,luma),1e-8);
}
// Surface readability fill is not a night-sky light source. Fade its use in
// participating media through twilight without changing extinction or lights.
vec3 scatteringIndirect(vec3 color,float sunHeight){
 return color*mix(.04,1.,smoothstep(-.16,.03,sunHeight));
}

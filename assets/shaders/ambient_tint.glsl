uniform vec3 uAmbientTint=vec3(1);
vec3 tintIndirect(vec3 color){
 vec3 tinted=color*uAmbientTint;
 const vec3 luma=vec3(.2126,.7152,.0722);
 return tinted*dot(color,luma)/max(dot(tinted,luma),1e-8);
}

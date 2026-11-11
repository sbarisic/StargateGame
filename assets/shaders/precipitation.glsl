float precipitationHeight(vec2 uv){
 float depth=texture(uShelter,uv).r;
 vec4 surface=uShelterInverse*vec4(uv*2.0-1.0,depth*2.0-1.0,1);
 return depth<.99999?surface.y/surface.w:-2050.0;
}

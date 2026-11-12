vec3 treeWindPosition(vec3 original,mat4 model){
 vec3 position=original;
    if(uTreeMode>0){
        float stem=clamp(position.y/max(uTreeHeight,1.),0.,1.);
        vec3 root=(model*vec4(0,0,0,1)).xyz;
        float gust=sin(dot(root.xz,vec2(.62,.37))-uTreeTime*1.8)+.35*sin(dot(root.xz,vec2(1.9,-1.3))-uTreeTime*3.1);
        vec2 wind=(inverse(mat3(model))*vec3(uTreeWind.x,0,uTreeWind.y)).xz;
        position.xz+=wind*gust*uTreeWindStrength*stem*stem;
        if(uTreeMode==2){float fine=sin(dot(original,vec3(8.1,5.7,3.4))+uTreeTime*3.3);position.xz+=wind*fine*.025*stem*(1.-smoothstep(25.,90.,length((uView*model*vec4(original,1)).xyz)));}
    }
 return position;
}

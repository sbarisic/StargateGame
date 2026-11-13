// Conservative coarse-to-fine emptiness query. Occupancy is max-reduced, never averaged.
float canopyEmptyDistance(vec3 position,vec3 ray,float sampleMip) {
    vec3 uv=canopyUv(position);
    if(!insideCanopy(uv))return 0.0;
    int densityMip=int(ceil(min(sampleMip,float(uCanopyLastMip))));
    // Entire trilinear footprint at BOTH density levels must remain in the empty node.
    vec3 halo=1.0/vec3(textureSize(uCanopyNormals,densityMip));
    vec2 amplitude=abs(bendAmplitude());
    float slope=2.0/max(uCanopyMaximum.y,1.0);
    vec3 speed=(abs(ray)+vec3(amplitude.x*slope*abs(ray.y),0,amplitude.y*slope*abs(ray.y)))/(uCanopyMaximum-uCanopyMinimum);
    int first=min(int(uCanopyLastMip),densityMip+5);
    for(int probe=0;probe<3;probe++) {
        int level=first-probe;
        if(level<=densityMip)break;
        ivec3 size=textureSize(uCanopy,level), cell=clamp(ivec3(floor(uv*vec3(size))),ivec3(0),size-1);
        if(texelFetch(uCanopy,cell,level).a!=0.0)continue;
        vec3 low=vec3(cell)/vec3(size)+halo, high=vec3(cell+1)/vec3(size)-halo;
        vec3 margin=min(uv-low,high-uv);
        if(any(lessThanEqual(margin,vec3(0))))continue;
        vec3 limits=margin/max(speed,vec3(1e-10));
        // Lipschitz bound includes the full height-dependent wind curve, not just its tangent.
        return max(0.0,min(limits.x,min(limits.y,limits.z))*.999);
    }
    return 0.0;
}

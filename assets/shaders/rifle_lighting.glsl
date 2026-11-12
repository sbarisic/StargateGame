vec3 rifleEnvironmentRay(vec3 ray) {
    // Sky/ground approximation. Never sample the viewmodel's render attachment.
    vec3 sky = skyColor(normalize(vec3(ray.x, max(.025, ray.y), ray.z)));
    return mix(ambientGroundColor() * 1.5, sky, smoothstep(-.12, .15, ray.y));
}
vec3 rifleEnvironment(vec3 base, vec3 normal, vec3 view, float rough, float metal) {
    vec3 reflected = reflect(-view, normal);
    vec3 axis = abs(reflected.y) < .95 ? vec3(0, 1, 0) : vec3(1, 0, 0);
    vec3 tangent = normalize(cross(axis, reflected)), bitangent = cross(reflected, tangent);
    float spread = rough * rough * .65;
    vec3 radiance = rifleEnvironmentRay(reflected) * .4;
    radiance += (rifleEnvironmentRay(normalize(reflected + tangent * spread)) +
        rifleEnvironmentRay(normalize(reflected - tangent * spread)) +
        rifleEnvironmentRay(normalize(reflected + bitangent * spread)) +
        rifleEnvironmentRay(normalize(reflected - bitangent * spread))) * .15;
    float nv = max(dot(normal, view), 0.0);
    vec3 f0 = mix(vec3(.04), base, metal);
    vec3 fresnel = f0 + (max(vec3(1.0 - rough), f0) - f0) * pow(1.0 - nv, 5.0);
    return radiance * fresnel * (1.0 - rough * .70);
}

// Rifle-only coated metal. UV detail follows animation and fades below pixel size.
uniform int uRifleFinish; // 0: original material, 1: receiver, 2: magazine

float rifleHash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}
float rifleNoise(vec2 p) {
    vec2 cell = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(rifleHash(cell), rifleHash(cell + vec2(1, 0)), f.x),
               mix(rifleHash(cell + vec2(0, 1)), rifleHash(cell + vec2(1)), f.x), f.y);
}
float rifleDetailVisibility(vec2 p) {
    float footprint = max(length(dFdx(p)), length(dFdy(p)));
    return 1.0 - smoothstep(.35, 1.25, footprint);
}
float rifleFilteredNoise(vec2 p) {
    return mix(.5, rifleNoise(p), rifleDetailVisibility(p));
}

vec2 rifleAtlasWear(vec2 uv, float tone) {
    // The authored atlas locates raised edges and dark screw/recess details.
    // Extract local contrast rather than outlining every triangle or UV seam.
    vec2 texel = 1.5 / vec2(textureSize(uTexture, 0));
    vec3 neighbours = texture(uTexture, uv + vec2(texel.x, 0), 1.0).rgb +
        texture(uTexture, uv - vec2(texel.x, 0), 1.0).rgb +
        texture(uTexture, uv + vec2(0, texel.y), 1.0).rgb +
        texture(uTexture, uv - vec2(0, texel.y), 1.0).rgb;
    float surrounding = dot(neighbours * .25, vec3(.2126, .7152, .0722));
    float raised = smoothstep(.012, .095, tone - surrounding);
    float recess = smoothstep(.008, .075, surrounding - tone);
    return vec2(raised, recess);
}

float rifleScratches(vec2 uv) {
    // Sparse finite scuffs, not stretched noise running the length of a UV island.
    // Neighbour cells allow jittered marks to cross cell edges without a grid seam.
    vec2 p = uv * 72.0, cell = floor(p);
    float footprint = max(length(dFdx(p)), length(dFdy(p)));
    float result = 0.0;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            vec2 key = cell + vec2(x, y);
            float seed = rifleHash(key + 7.3);
            vec2 centre = key + vec2(.15) + .7 * vec2(rifleHash(key + 21.1), rifleHash(key + 43.7));
            vec2 direction = vec2(rifleHash(key + 19.2), rifleHash(key + 61.4)) * 2.0 - 1.0;
            direction /= max(length(direction), .001);
            vec2 offset = p - centre;
            float halfLength = mix(.055, .32, rifleHash(key + 83.5));
            float along = dot(offset, direction) / halfLength;
            float bend = (rifleHash(key + 97.4) - .5) * .045 * (1.0 - min(along * along, 1.0));
            float across = abs(dot(offset, vec2(-direction.y, direction.x)) - bend);
            float width = mix(.004, .012, rifleHash(key + 32.8));
            float edge = max(footprint * .65, .001);
            float stroke = (1.0 - smoothstep(width, width + edge, across)) *
                (1.0 - smoothstep(.55, 1.0, abs(along)));
            stroke *= width / (width + edge);
            float broken = smoothstep(.24, .58, rifleNoise(p * 17.0 + key));
            float strength = smoothstep(.72, .95, seed) * mix(.3, 1.0, rifleHash(key + 12.8));
            result = max(result, stroke * broken * strength);
        }
    }
    return result * (1.0 - smoothstep(.15, .5, footprint));
}

void rifleFinish(vec2 uv, vec3 position, inout vec3 albedo, inout vec3 normal,
                 out float roughness, out float metal) {
    // Keep source part/cartridge tones without its large pixel-painted patches.
    vec3 source = texture(uTexture, uv, 1.0).rgb;
    float tone = dot(source, vec3(.2126, .7152, .0722));
    float grain = rifleFilteredNoise(uv * 1536.0);
    vec2 warp = vec2(rifleNoise(uv * 67.0), rifleNoise(uv * 67.0 + 13.4)) * 2.0;
    float brush = rifleFilteredNoise(uv * vec2(950, 160) + warp);
    float tooling = rifleFilteredNoise(uv * 145.0 + warp);
    float handling = rifleNoise(uv * 23.0);
    float scratches = rifleScratches(uv);
    vec2 atlasWear = rifleAtlasWear(uv, tone);
    float mottling = rifleNoise(uv * 91.0 + warp);
    float edgeWear = atlasWear.x * smoothstep(.28, .76, mottling);
    float wear = clamp(smoothstep(.12, .46, tone) * .09 + scratches * .20 + edgeWear * .22, 0.0, .38);
    float smudges = smoothstep(.43, .76, handling) * smoothstep(.26, .72, mottling);
    float grime = clamp(atlasWear.y * (.35 + mottling * .45) + smudges * .24, 0.0, .8);
    float oil = smoothstep(.60, .83, rifleNoise(uv * 37.0 + 8.6)) * (1.0 - grime);
    bool magazine = uRifleFinish == 2;
    vec3 coating = magazine ? vec3(.022, .026, .030) : vec3(.027, .032, .038);
    albedo = coating * (1.0 + (grain - .5) * .10 + (tooling - .5) * .06 + (handling - .5) * .08);
    albedo = mix(albedo, vec3(.16, .17, .18), wear);
    // Dark dusty deposits collect in authored recesses, while handling leaves
    // faint, smoother oil patches. Avoid a uniform brown wash over the weapon.
    albedo = mix(albedo, vec3(.012, .010, .008), grime * .65);
    albedo *= 1.0 - oil * .10;
    // Matte coating dominates; exposed wear keeps restrained highlights.
    roughness = clamp((magazine ? .84 : .78) + (brush - .5) * .04 +
        (grain - .5) * .05 + (handling - .5) * .06 - scratches * .04 -
        edgeWear * .07 + grime * .12 - oil * .025, .62, .94);
    metal = mix(.02, .65, wear) * (1.0 - grime * .65);

    float brass = magazine ? smoothstep(.08, .22, source.r - source.b) *
        smoothstep(.025, .12, source.g - source.b) : 0.0;
    albedo = mix(albedo, vec3(.30, .19, .055), brass);
    roughness = mix(roughness, .46, brass);
    metal = mix(metal, .95, brass);

    // Surface-gradient bumping follows the skinned frame, including mirrored UVs.
    float height = (grain - .5) * .000010 + (brush - .5) * .000006 - scratches * .000008;
    vec3 dx = dFdx(position), dy = dFdy(position);
    vec3 r1 = cross(dy, normal), r2 = cross(normal, dx);
    float determinant = dot(dx, r1);
    vec3 gradient = (dFdx(height) * r1 + dFdy(height) * r2) *
        sign(determinant) / max(abs(determinant), 1e-12);
    gradient *= min(1.0, .18 / max(length(gradient), 1e-6));
    normal = normalize(normal - gradient);
}

vec3 rifleSunBrdf(vec3 base, vec3 normal, vec3 view, vec3 sun, float rough, float metal) {
    float nl = max(dot(normal, sun), 0.0), nv = max(dot(normal, view), .001);
    vec3 sum = view + sun;
    vec3 halfway = sum * inversesqrt(max(dot(sum, sum), 1e-8));
    float nh = max(dot(normal, halfway), 0.0), vh = max(dot(view, halfway), 0.0);
    float alpha = rough * rough, alpha2 = alpha * alpha;
    float denominator = nh * nh * (alpha2 - 1.0) + 1.0;
    float distribution = alpha2 / max(3.14159265 * denominator * denominator, 1e-6);
    float k = (rough + 1.0) * (rough + 1.0) * .125;
    float geometry = nv / (nv * (1.0 - k) + k) * nl / max(nl * (1.0 - k) + k, .001);
    vec3 f0 = mix(vec3(.04), base, metal);
    vec3 fresnel = f0 + (1.0 - f0) * pow(1.0 - vh, 5.0);
    // Same irradiance convention as terrainBrdf: cancel diffuse's 1/pi.
    vec3 specular = distribution * geometry * fresnel / max(4.0 * nv * nl, .001);
    return ((1.0 - fresnel) * base * (1.0 - metal) + specular * 3.14159265) * nl;
}

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

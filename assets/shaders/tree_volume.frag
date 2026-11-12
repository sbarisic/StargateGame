#version 400 core
in vec2 vUv;
#include "gbuffer_outputs.glsl"

uniform vec3 uViewPosition, uCanopyMinimum, uCanopyMaximum;
uniform mat4 uInverseViewProjection, uCanopyViewProjection, uTreeWorld, uTreeInverse;
uniform vec3 uCanopyRoot;
uniform sampler3D uCanopy, uCanopyNormals;
uniform float uCellSize, uDensityReferenceDistance, uDensityScale, uCanopyTime, uWindStrength;
uniform float uPrimaryStepCells, uOpacityThreshold, uTransmissionStrength;
uniform float uCanopyCoverage, uCanopyScale;
uniform vec4 uCanopyLod; // mip, last resident mip, march limit, sunlight samples
#define uCanopyMip uCanopyLod.x
#define uCanopyLastMip uCanopyLod.y
#define uMarchLimit int(uCanopyLod.z)
#define uLightSteps uCanopyLod.w
uniform vec2 uCanopyWind;
uniform vec3 uLeafTint;
uniform float uLeafSaturation;
#include "daylight.glsl"

vec2 bendAmplitude() {
    float gust = sin(dot(uCanopyRoot.xz, vec2(.62, .37)) - uCanopyTime * 1.8) + .35 * sin(dot(uCanopyRoot.xz, vec2(1.9, -1.3)) - uCanopyTime * 3.1);
    return uCanopyWind * gust * uWindStrength;
}
vec3 canopyUv(vec3 position) {
    float height = clamp(position.y / max(uCanopyMaximum.y, 1.0), 0.0, 1.0);
    position.xz -= bendAmplitude() * height * height;
    return (position - uCanopyMinimum) / (uCanopyMaximum - uCanopyMinimum);
}
bool insideCanopy(vec3 uv) {
    return all(greaterThanEqual(uv, vec3(0))) && all(lessThanEqual(uv, vec3(1)));
}
bool canopyInterval(vec3 origin, vec3 direction, out float entry, out float exit) {
    // Exact parallel slabs: replacing a small negative component with positive
    // epsilon changes the interval and can cut holes into an almost parallel ray.
    vec2 amplitude = abs(bendAmplitude());
    vec3 margin = vec3(amplitude.x, 0, amplitude.y) + vec3(uCellSize);
    vec3 lower = uCanopyMinimum - margin, upper = uCanopyMaximum + margin;
    entry = 0.0;
    exit = 1e20;
    for (int axis = 0; axis < 3; axis++) {
        if (abs(direction[axis]) < 1e-7) {
            if (origin[axis] < lower[axis] || origin[axis] > upper[axis]) return false;
        } else {
            float a = (lower[axis] - origin[axis]) / direction[axis];
            float b = (upper[axis] - origin[axis]) / direction[axis];
            entry = max(entry, min(a, b));
            exit = min(exit, max(a, b));
            if (exit <= entry) return false;
        }
    }
    return exit > entry;
}
float extinction(vec3 uv, float mip) {
    if (!insideCanopy(uv)) return 0.0;
    // Alpha here encodes mean optical depth, NOT maximum occupancy.
    float opacity = textureLod(uCanopyNormals, uv, mip).a;
    return -log(max(1.0 - opacity, 1.0 / 255.0)) / uDensityReferenceDistance * uDensityScale;
}
float canopyTransmission(vec3 position, vec3 toward, float count) {
    float entry, exit;
    // Start beyond the surface voxel to avoid treating its own thickness twice.
    vec3 origin = position + toward * uCellSize;
    if (!canopyInterval(origin, toward, entry, exit)) return 1.0;
    float stepLength = (exit - entry) / count;
    float mip = clamp(log2(max(stepLength / uCellSize, 1.0)), 0.0, 8.0);
    float opticalDepth = 0.0;
    for (int i = 0; i < 24; i++) {
        float start = float(i) * stepLength;
        if (start >= exit - entry) break;
        float segment = min(stepLength, exit - entry - start);
        vec3 uv = canopyUv(origin + toward * (entry + start + segment * .5));
        opticalDepth += extinction(uv, mip) * segment;
    }
    return exp(-opticalDepth);
}
vec3 deformedNormal(vec3 normal, vec3 position) {
    // Inverse-transpose of xz += amplitude * (y / height)^2.
    float height = max(uCanopyMaximum.y, 1.0);
    float derivative = position.y > 0.0 && position.y < height ? 2.0 * position.y / (height * height) : 0.0;
    normal.y -= dot(normal.xz, bendAmplitude() * derivative);
    return dot(normal, normal) > 1e-8 ? normalize(normal) : vec3(0, 1, 0);
}
void main() {
    vec4 farPoint = uInverseViewProjection * vec4(vUv * 2.0 - 1.0, 1, 1);
    vec3 worldRay = normalize(farPoint.xyz / farPoint.w - uViewPosition);
    vec3 ray = mat3(uTreeInverse) * worldRay;
    vec3 eye = (uTreeInverse * vec4(uViewPosition, 1)).xyz;
    float distance, end;
    if (!canopyInterval(eye, ray, distance, end)) discard;
    // Cover the whole interval even when a long diagonal exhausts the LOD budget.
    // Filter to the actual pixel footprint as well as the species LOD. Thin,
    // distant crowns otherwise retain subpixel density and sparkle on movement.
    float footprint = max(length(dFdx(ray)), length(dFdy(ray))) * end;
    float filteredMip = max(uCanopyMip, log2(max(1.0, footprint / uCellSize)));
    // Reserve the partial entry segment plus one floating-point boundary step.
    float stepLength = max(uCellSize * uPrimaryStepCells * exp2(filteredMip), (end - distance) / float(max(3, uMarchLimit) - 2));
    float sampleMip = min(float(uCanopyLastMip), max(filteredMip, log2(max(1.0, stepLength / (uCellSize * uPrimaryStepCells)))));
    float occupancyMip = min(float(uCanopyLastMip), floor(sampleMip) + 2.0);
    float stretch = 1.0 + length(bendAmplitude()) * 2.0 / max(uCanopyMaximum.y, 1.0);
    float safeSkip = uCellSize * 1.5 * exp2(max(0.0, occupancyMip - 2.0)) / stretch;
    float targetDepth = -log(1.0 - uOpacityThreshold);
    float opticalDepth = 0.0;
    // Anchor integration planes in tree space. Camera translation and a change
    // of occupancy mip must not restart the sample pattern at the box face.
    float rayOrigin = dot(eye, ray);
    float nextPlane = (floor((distance + rayOrigin) / stepLength) + 1.0) * stepLength - rayOrigin;
    vec3 position = vec3(0), uv = vec3(0);
    bool hit = false;
    for (int step = 0; step < 1536 && step < uMarchLimit && distance < end; step++) {
        float segment = min(max(nextPlane - distance, 0.000001), end - distance);
        nextPlane += stepLength;
        vec3 midpoint = eye + ray * (distance + segment * .5);
        uv = canopyUv(midpoint);
        if (!insideCanopy(uv)) { distance += segment; continue; }
        // Zero in the conservative pyramid is empty. Do not discard low but
        // nonzero coverage, and bound skipping under the wind deformation.
        if (textureLod(uCanopy, uv, occupancyMip).a == 0.0) {
            if (safeSkip >= segment) {
                float extra = floor((safeSkip - segment) / stepLength) * stepLength;
                distance += min(end - distance, segment + extra);
                nextPlane += extra;
                continue;
            }
        }
        float sigma = extinction(uv, sampleMip);
        if (opticalDepth + sigma * segment >= targetDepth) {
            // Locate the threshold within this segment rather than writing the
            // sample centre's depth. Foreground opaque geometry stays in front.
            distance += (targetDepth - opticalDepth) / max(sigma, 1e-8);
            position = eye + ray * distance;
            uv = canopyUv(position);
            hit = true;
            break;
        }
        opticalDepth += sigma * segment;
        distance += segment;
    }
    if (!hit) discard;
    // Complementary with the source-local mesh LOD mask; stable while moving the camera.
    vec3 restPosition = mix(uCanopyMinimum, uCanopyMaximum, uv) / uCanopyScale;
    float lodNoise = fract(sin(dot(floor(restPosition * 96.0), vec3(12.9898, 78.233, 39.425))) * 43758.5453);
    if (lodNoise >= uCanopyCoverage) discard;
    vec4 leaf = textureLod(uCanopy, uv, sampleMip);
    vec3 normal = mat3(uTreeWorld) * deformedNormal(textureLod(uCanopyNormals, uv, sampleMip).xyz * 2.0 - 1.0, position);
    vec3 worldPosition = (uTreeWorld * vec4(position, 1)).xyz;
    // Source leaf normals are unoriented axes. Never flip them toward the eye:
    // doing so turns the upper crown's normals downward when viewed from below.
    float diffuse = abs(dot(normal, uSunDirection));
    vec3 biasNormal = dot(normal, uSunDirection) < 0.0 ? -normal : normal;
    float sunTransmission = canopyTransmission(position, mat3(uTreeInverse) * uSunDirection, uLightSteps);
    vec3 albedo=decodeColor(leaf.rgb);
    albedo=mix(vec3(dot(albedo,vec3(.2126,.7152,.0722))),albedo,uLeafSaturation)*uLeafTint;
    vec4 clip=uCanopyViewProjection*vec4(worldPosition,1);float depth=clip.z/clip.w*.5+.5;
    vec3 geometric=dot(normal,-worldRay)<0.?-normal:normal;
    writeSurface(albedo,.92,normal,0.,0.,geometric,uTransmissionStrength,5.,vec3(0),sunTransmission,depth,depth);
}

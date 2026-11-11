// Adapted from FishGfx voxel.frag; see THIRD-PARTY-NOTICES.md.
uniform int uShadowEnabled;
uniform int uShadowComponents; // Inspection only: 1 static, 2 dynamic, 0 combined.
uniform int uShadowCascadeCount;
uniform float uShadowStrength;
uniform float uShadowSurfaceBias;
uniform float uShadowBlendFraction;
uniform int uShadowFilterRadius;
uniform mat4 uShadowMatrices[3];
uniform float uShadowSplits[3];
uniform float uShadowDepthRanges[3];
uniform float uShadowMapDepthRanges[3];
uniform float uShadowWorldTexelSizes[3];
uniform sampler2DShadow uShadowMaps[3];
uniform sampler2DShadow uDynamicShadowMaps[3];
uniform int uViewmodelShadowEnabled;
uniform sampler2D uViewmodelShadow;
uniform mat4 uViewmodelShadowMatrix;

// Interpolate visibility, not depth: linear depth sampling still produces a
// hard comparison edge. Correct each comparison to the receiver's plane.
float viewmodelBilinearVisibility(vec2 uv, float depth, vec2 gradient) {
    ivec2 size = textureSize(uViewmodelShadow, 0);
    vec2 pixel = uv * vec2(size) - .5;
    ivec2 origin = ivec2(floor(pixel));
    vec2 fraction = fract(pixel);
    float result = 0.;
    for (int y=0;y<2;y++) for (int x=0;x<2;x++) {
        ivec2 cell = clamp(origin + ivec2(x,y), ivec2(0), size-ivec2(1));
        vec2 center = (vec2(cell)+.5)/vec2(size);
        float weight = (x==0?1.-fraction.x:fraction.x)*(y==0?1.-fraction.y:fraction.y);
        result += weight * (depth + dot(gradient, center-uv) <= texelFetch(uViewmodelShadow, cell, 0).r ? 1. : 0.);
    }
    return result;
}

float viewmodelDynamicVisibility(vec3 position, vec3 normal, vec3 correctionNormal) {
    vec3 q = (uViewmodelShadowMatrix * vec4(position + normal * .002, 1)).xyz * .5 + .5;
    if (any(lessThan(q, vec3(0))) || any(greaterThan(q, vec3(1)))) return 1;
    vec3 plane = transpose(inverse(mat3(uViewmodelShadowMatrix))) * correctionNormal;
    vec2 gradient = abs(plane.z) > .00001 ? clamp(-plane.xy / plane.z, vec2(-2), vec2(2)) : vec2(0);
    vec2 texel = 1.0 / vec2(textureSize(uViewmodelShadow, 0));
    float result = 0;
    for (int y=-1;y<=1;y++) for (int x=-1;x<=1;x++) {
        vec2 delta = vec2(x,y) * texel * 1.5;
        float depth = q.z - .00002 + dot(gradient, delta);
        float weight = (x==0?2.:1.)*(y==0?2.:1.);
        result += weight * viewmodelBilinearVisibility(q.xy + delta, depth, gradient);
    }
    return result / 16.;
}

// GLSL 4.0 sampler-array indices must be dynamically uniform. Cascade selection
// varies per fragment, so use constant sampler indices (including at split edges).
float staticSample(int c,vec3 p) {
    if(c==0)return texture(uShadowMaps[0],p);
    if(c==1)return texture(uShadowMaps[1],p);
    return texture(uShadowMaps[2],p);
}
float dynamicSample(int c,vec3 p) {
    if(c==0)return texture(uDynamicShadowMaps[0],p);
    if(c==1)return texture(uDynamicShadowMaps[1],p);
    return texture(uDynamicShadowMaps[2],p);
}

float SampleShadowCascade(int cascade, vec3 worldPosition, vec3 normal, vec3 correctionNormal, float nDotL)
{
	float slope = 1.0 - clamp(nDotL, 0.0, 1.0);
	float worldTexelSize = uShadowWorldTexelSizes[cascade];
	float normalOffset = min(.02 + worldTexelSize * 1.5 * slope, 0.2);
	vec3 receiverPosition = worldPosition + normal * normalOffset;
	vec4 projected = uShadowMatrices[cascade] * vec4(receiverPosition, 1.0);

	if (projected.w <= 0.0)
	{
		return 1.0;
	}

	vec3 shadowCoordinate = projected.xyz / projected.w;
	vec2 uv = shadowCoordinate.xy * 0.5 + 0.5;
	float receiverDepth = shadowCoordinate.z * 0.5 + 0.5;

	if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0
		|| receiverDepth < 0.0 || receiverDepth > 1.0)
	{
		return 1.0;
	}

	float worldBias = uShadowSurfaceBias + worldTexelSize * 0.2 * slope;
	float bias = max(worldBias * 0.5 / max(uShadowMapDepthRanges[cascade], 1.0), 0.000002);
	vec2 texel = 1.0 / vec2(textureSize(uShadowMaps[0], 0));
	vec2 minimumUv = texel * 0.5;
	vec2 maximumUv = vec2(1.0) - minimumUv;
	// Match comparison depth to the actual receiver triangle across the PCF footprint.
    // Use the triangle plane rather than interpolated lighting normals for PCF depth correction.
    vec3 planeNormal = transpose(inverse(mat3(uShadowMatrices[cascade]))) * correctionNormal;
    vec2 gradient = abs(planeNormal.z) > .00001 ? clamp(-planeNormal.xy / planeNormal.z, vec2(-2), vec2(2)) : vec2(0);
    float comparisonDepth = receiverDepth - bias - dot(abs(gradient), texel) * .5;
	float staticVisibility = 0.0;
	if (uShadowFilterRadius <= 1)
	{
		const vec2 offsets[4] = vec2[4](
			vec2(-0.5, -0.5), vec2(0.5, -0.5),
			vec2(-0.5, 0.5), vec2(0.5, 0.5));
		for (int sampleIndex = 0; sampleIndex < 4; sampleIndex++)
		{
			vec2 sampleUv = clamp(uv + offsets[sampleIndex] * texel, minimumUv, maximumUv);
			staticVisibility += staticSample(cascade, vec3(sampleUv, comparisonDepth + dot(gradient, sampleUv - uv)));
		}
		staticVisibility *= 0.25;
	}
	else
	{
		for (int y = -1; y <= 1; y++)
		{
			for (int x = -1; x <= 1; x++)
			{
				vec2 sampleUv = clamp(uv + vec2(x, y) * texel * 1.5, minimumUv, maximumUv);
				staticVisibility += staticSample(cascade, vec3(sampleUv, comparisonDepth + dot(gradient, sampleUv - uv)));
			}
		}
		staticVisibility /= 9.0;
	}
	float dynamicVisibility = dynamicSample(
		cascade,
		vec3(clamp(uv, minimumUv, maximumUv), comparisonDepth));
    if (uViewmodelShadowEnabled != 0)
        dynamicVisibility = viewmodelDynamicVisibility(worldPosition, normal, correctionNormal);
	return uShadowComponents==1?staticVisibility:uShadowComponents==2?dynamicVisibility:min(staticVisibility, dynamicVisibility);
}

float SampleSunVisibilityNormals(vec3 worldPosition, vec3 normal, vec3 correctionNormal, float nDotL)
{
	if (uShadowEnabled == 0 || uShadowCascadeCount == 0)
	{
		return 1.0;
	}

	float viewDepth = length(worldPosition - uViewPosition);
	int cascade = uShadowCascadeCount - 1;

	for (int index = 0; index < uShadowCascadeCount; index++)
	{
		if (viewDepth <= uShadowSplits[index])
		{
			cascade = index;
			break;
		}
	}

	float visibility = SampleShadowCascade(cascade, worldPosition, normal, correctionNormal, nDotL);

	if (cascade + 1 < uShadowCascadeCount)
	{
		float blendWidth = max(uShadowDepthRanges[cascade] * uShadowBlendFraction, 0.0001);
		float blendStart = uShadowSplits[cascade] - blendWidth;
		float blend = clamp((viewDepth - blendStart) / blendWidth, 0.0, 1.0);

		if (blend > 0.0)
		{
			float nextVisibility = SampleShadowCascade(cascade + 1, worldPosition, normal, correctionNormal, nDotL);
			visibility = mix(visibility, nextVisibility, blend);
		}
	}

	float fade = 1.0 - smoothstep(uShadowSplits[uShadowCascadeCount-1] * .85, uShadowSplits[uShadowCascadeCount-1], viewDepth);
	float farVisibility = farSunVisibility(worldPosition);
	return min(farVisibility, mix(1.0, visibility, uShadowStrength * fade));
}

// Existing receivers use their geometric plane for both operations. Arms explicitly
// supply their smooth skinned normal for the offset and triangle plane for PCF.
float SampleSunVisibility(vec3 p, vec3 n, float cosine) {
    return SampleSunVisibilityNormals(p,n,n,cosine);
}

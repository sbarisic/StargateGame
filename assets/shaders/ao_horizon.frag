#version 400 core
in vec2 vUv;
out vec4 result;
#include "ao_common.glsl"
uniform float uRadius, uProjectionScale;
uniform int uSlices, uSteps;
const float PI = 3.14159265359;
void main() {
    float depth = texture(uDepth, vUv).r;
    vec4 materialNormal = texture(uNormals, vUv);
    if (depth >= .99999 || materialClass(materialNormal) < .5) { result=vec4(1); return; }
    vec3 position = surfacePosition(vUv, depth);
    vec3 view = normalize(uEye-position);
    vec3 normal = normalize(materialNormal.xyz*2.-1.);
    if (dot(normal,view)<0.) normal=-normal;
    vec2 pixel = 1.0 / vec2(textureSize(uDepth,0));
    float radiusPixels = min(160., uRadius*uProjectionScale/max(length(position-uEye),.1));
    // Stationary interleaving: no history or frame-dependent shimmer.
    vec2 cell=floor(gl_FragCoord.xy);
    float rotation=fract(dot(cell,vec2(.06711056,.00583715))*52.9829189)*PI;
    float visibility=0.;
    for (int slice=0; slice<3; slice++) {
        if(slice>=uSlices) break;
        float angle=rotation+PI*float(slice)/float(uSlices);
        vec2 direction=vec2(cos(angle),sin(angle));
        vec3 tangent=surfacePosition(vUv+direction*pixel,depth)-position;
        tangent=normalize(tangent-view*dot(tangent,view));
        vec3 axis=normalize(cross(tangent,view));
        vec3 projected=normal-axis*dot(normal,axis);
        float projectedLength=length(projected);
        float n=atan(dot(projected,tangent),dot(projected,view));
        vec2 horizons=vec2(-1);
        for(int side=0;side<2;side++) {
            float signSide=side==0?1.:-1.;
            for(int step=0;step<6;step++) {
                if(step>=uSteps) break;
                float fraction=(float(step)+.75)/float(uSteps);
                vec2 sampleUv=vUv+direction*signSide*pixel*max(1.5,radiusPixels*fraction*fraction);
                if(any(lessThan(sampleUv,vec2(0)))||any(greaterThan(sampleUv,vec2(1)))) continue;
                float sampleDepth=texture(uDepth,sampleUv).r;
                if(sampleDepth>=.99999) continue;
                vec3 delta=surfacePosition(sampleUv,sampleDepth)-position;
                float distance=length(delta);
                if(distance<.015||distance>=uRadius) continue;
                float weight=occluderWeight(materialClass(texture(uNormals,sampleUv)));
                weight*=1.-smoothstep(uRadius*.35,uRadius,distance);
                // Reduce single-layer depth's implied thickness behind foreground objects.
                weight*=1.-.5*smoothstep(.15,.8,dot(delta,view)/uRadius);
                horizons[side]=max(horizons[side],mix(-1.,dot(delta/distance,view),weight));
            }
        }
        float h0=n+clamp(acos(clamp(horizons.x,-1.,1.))-n,-PI*.5,PI*.5);
        float h1=n+clamp(-acos(clamp(horizons.y,-1.,1.))-n,-PI*.5,PI*.5);
        visibility+=projectedLength*.25*(2.*cos(n)-cos(2.*h0-n)-cos(2.*h1-n)+2.*(h0+h1)*sin(n));
    }
    result=vec4(clamp(visibility/float(uSlices),0.,1.));
}

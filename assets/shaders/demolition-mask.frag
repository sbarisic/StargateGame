#version 400 core
uniform sampler2D uTexture, uSceneDepth;
uniform float uCutoff;
in vec2 vUv;
out float mask;
void main(){
    float depth=texelFetch(uSceneDepth,ivec2(gl_FragCoord.xy),0).r;
    // Compare against the rendered scene, with a small depth precision allowance.
    // No coincident transparent polygons or changes to the scene depth buffer.
    if(gl_FragCoord.z>depth+0.0000005)discard;
    if(texture(uTexture,vUv).a<uCutoff)discard;
    mask=1.;
}

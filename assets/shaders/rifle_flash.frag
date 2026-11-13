#version 400 core
uniform sampler2D uWeaponDepth;
uniform int uViewmodel;
uniform float uFlashSeed,uFlashEnergy;
in vec4 vColor;
in vec2 vUv;
out vec4 fragColor;
void main()
{
    // Same viewport and projection as the viewmodel G-buffer. Its MSAA guide
    // resolves the nearest covered sample, keeping the barrel edge opaque.
    if (uViewmodel != 0 && gl_FragCoord.z > texelFetch(uWeaponDepth, ivec2(gl_FragCoord.xy), 0).r)
        discard;
    float heat;
    if(vColor.a<.5) {
        float r=length(vUv*2.-1.);
        heat=pow(max(0.,1.-r),2.);
    } else {
        float along=vUv.x;
        float ripple=sin(along*29.+uFlashSeed)*.07+sin(along*57.-uFlashSeed*2.)*.035;
        float width=(.25+.65*sin(along*3.14159))*(1.-along);
        float edge=abs(vUv.y*2.-1.+ripple);
        heat=(1.-smoothstep(width*.25,width+.001,edge))*pow(1.-along,1.6);
        heat*=smoothstep(0.,.035,along);
    }
    // HDR emission: white-yellow core, amber edge, soft falloff to no energy.
    vec3 color=mix(vec3(1.,.16,.015),vec3(1.,.84,.5),smoothstep(.1,.75,heat));
    fragColor=vec4(color*heat*uFlashEnergy*5.,0.);
}

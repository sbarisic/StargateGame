#version 400 core
in vec2 vUv;layout(location=0) out vec4 guideNormal;
uniform sampler2DMS uGDepth,uGGeometry;
void main(){ivec2 pixel=ivec2(gl_FragCoord.xy);float nearest=1.;vec4 chosen=vec4(0);
 for(int i=0;i<4;i++){float d=texelFetch(uGDepth,pixel,i).r;vec4 g=texelFetch(uGGeometry,pixel,i);if(g.a>.5&&d<nearest){nearest=d;chosen=g;}}
 guideNormal=chosen;gl_FragDepth=nearest;
}

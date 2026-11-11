#version 400 core
in vec2 vUv;out vec4 fragColor;
uniform sampler2D uTexture;
uniform vec2 uBlurStep;
uniform sampler2D uViewmodelCoverage;
uniform int uMaskViewmodel;
vec3 sourceAt(vec2 uv){vec3 c=texture(uTexture,uv).rgb;return uMaskViewmodel!=0?c*(1.0-texture(uViewmodelCoverage,uv).a):c;}
void main(){
 vec3 c=sourceAt(vUv)*.227027;
 c+=(sourceAt(vUv+uBlurStep*1.384615)+sourceAt(vUv-uBlurStep*1.384615))*.316216;
 c+=(sourceAt(vUv+uBlurStep*3.230769)+sourceAt(vUv-uBlurStep*3.230769))*.070270;
 fragColor=vec4(c,1);
}

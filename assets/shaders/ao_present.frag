#version 400 core
in vec2 vUv;out vec4 result;uniform sampler2D uTexture;
void main(){result=texture(uTexture,vUv);}

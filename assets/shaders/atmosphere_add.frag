#version 400 core
in vec2 vUv;uniform sampler2D uA,uB;out vec4 result;void main(){result=texture(uA,vUv)+texture(uB,vUv);}

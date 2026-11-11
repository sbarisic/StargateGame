#version 400 core
layout(location=0) in vec3 aPosition;
out vec2 vUv;
void main(){ gl_Position=vec4(aPosition.xy,0,1); vUv=aPosition.xy*.5+.5; }

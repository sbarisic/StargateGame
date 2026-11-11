#version 400 core
in vec2 vUv;out vec4 value;
uniform sampler2D uState;uniform float uHeightScale;
void main(){vec2 s=texture(uState,vUv).rg;value=vec4(max(s.x,0.0)*uHeightScale,max(-s.x,0.0)*uHeightScale,abs(s.y)*10.0,(any(isnan(s))||any(isinf(s)))?0.0:1.0);}

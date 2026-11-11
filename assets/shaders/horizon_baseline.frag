#version 400 core
in vec2 vUv;out float height;
uniform float uRadius,uSampleTime,uWaveScale,uDistortion,uSpeed;
#include "horizon_baseline.glsl"
void main(){height=baseline((vUv-.5)*2.0*uRadius,uSampleTime*uSpeed,0.0).x;}

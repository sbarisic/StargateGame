#version 400 core
#include "terrain_coverage.glsl"
uniform sampler2D uTexture;
uniform float uFoliageCutoff;
in vec2 vUv;
uniform vec4 uTravelPlane;
in vec3 vTravelWorld;
void main(){terrainCoverage(vTravelWorld);if(dot(vec4(vTravelWorld,1.0),uTravelPlane)<0.0)discard;if(uFoliageCutoff>0.0&&texture(uTexture,vUv).a<uFoliageCutoff)discard;}

// Complementary world-stable masks hand off land and water together.
uniform sampler2D uTerrainCoverage;
uniform int uTerrainCoverageLevel;
uniform float uTerrainCoverageTime;
void terrainCoverage(vec3 world){
 if(uTerrainCoverageLevel==0)return;
 ivec2 cell=clamp(ivec2(floor((world.xz+2048.)/64.)),ivec2(0),ivec2(63));
 vec4 c=texelFetch(uTerrainCoverage,cell,0);
 float blend=clamp((uTerrainCoverageTime-c.z)/.35,0.,1.);
 uvec2 p=uvec2(ivec2(floor(world.xz*32.)));uint h=p.x*73856093u^p.y*19349663u;h^=h>>16;h*=0x7feb352du;h^=h>>15;
 float owner=float(h&65535u)/65536.<blend?c.y:c.x;
 if(abs(owner-float(uTerrainCoverageLevel))>.25)discard;
}

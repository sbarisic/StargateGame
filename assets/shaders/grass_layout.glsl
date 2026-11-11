// The matching mesh and volume use the same deterministic quarter-turn per
// patch. Square-preserving rotations avoid gaps and change the repeated motif.
mat2 grassRotation(vec2 origin){
 ivec2 cell=ivec2(floor(origin*.5));
 uint h=uint(cell.x)*73856093u^uint(cell.y)*19349663u;
 h^=h>>16;h*=0x7feb352du;h^=h>>15;
 uint turn=h&3u;
 if(turn==0u)return mat2(1,0,0,1);
 if(turn==1u)return mat2(0,1,-1,0);
 if(turn==2u)return mat2(-1,0,0,-1);
 return mat2(0,-1,1,0);
}

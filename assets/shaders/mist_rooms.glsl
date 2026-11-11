#ifndef MIST_ROOMS
#define MIST_ROOMS
uniform int uRoomCount;
uniform vec4 uRoomBounds[32],uRoomEdges[128];
float outsideMist(vec3 p){
 for(int i=0;i<uRoomCount;i++){
  if(p.y<uRoomBounds[i].x||p.y>uRoomBounds[i].y)continue;
  bool inside=true;
  for(int e=0;e<4;e++)if(dot(uRoomEdges[i*4+e],vec4(p,1))<0)inside=false;
  if(inside)return 0;
 }
 return 1;
}
#endif

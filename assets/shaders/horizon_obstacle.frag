#version 400 core
in vec2 vUv;out vec4 mask;
uniform vec2 uVertices[80];uniform int uVertexCount;
uniform float uRadius,uCell;uniform vec4 uChannels;
void main(){
 vec2 p=(vUv-.5)*2.0*uRadius;
 float distance=-1000.0;
 for(int i=0;i<uVertexCount;i++){
  vec2 a=uVertices[i],b=uVertices[(i+1)%uVertexCount],edge=b-a;
  distance=max(distance,dot(p-a,vec2(edge.y,-edge.x))/max(length(edge),.00001));
 }
 float coverage=1.0-smoothstep(-uCell*.7,uCell*.7,distance);
 // Channels combine by 1-(1-a)(1-b): a union, including overlapping obstacles.
 mask=coverage*uChannels;
 // Entry impulses have a metre-space width, independent of grid resolution.
 // Persistent obstacles still use the sharp antialiased coverage above.
 float contour=exp(-(distance/.25)*(distance/.25));
 mask.b=contour*uChannels.b;
 mask.a=contour*uChannels.a;
}

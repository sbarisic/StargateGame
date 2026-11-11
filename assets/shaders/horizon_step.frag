#version 400 core
in vec2 vUv;out vec2 nextState;
uniform sampler2D uState,uObstacles,uPreviousObstacles,uBaselineStart,uBaselineEnd;
uniform float uCell,uDt,uPhase,uDamping,uResponse,uRadius,uSpeedSquared,uMotionStep;
uniform int uFirstSubstep,uPacket;
uniform float uImpulseAmplitude,uImpulseHeight,uMotionAmplitude;
uniform float uContactTime;
float baseAt(ivec2 p){return mix(texelFetch(uBaselineStart,p,0).r,texelFetch(uBaselineEnd,p,0).r,uPhase);}
vec2 fluidState(ivec2 p){
 vec2 s=texelFetch(uState,p,0).rg;
 if(uFirstSubstep==0 || texelFetch(uPreviousObstacles,p,0).r<.5 || texelFetch(uObstacles,p,0).r>=.5)return s;
 // Extend nearby fluid into newly uncovered cells; never release stored obstacle energy.
 vec2 sum=vec2(0);float count=0.0;ivec2 size=textureSize(uState,0);
 ivec2 offsets[4]=ivec2[4](ivec2(1,0),ivec2(-1,0),ivec2(0,1),ivec2(0,-1));
 for(int i=0;i<4;i++){
  ivec2 q=clamp(p+offsets[i],ivec2(0),size-1);
  if(texelFetch(uPreviousObstacles,q,0).r<.5 && texelFetch(uObstacles,q,0).r<.5){sum+=texelFetch(uState,q,0).rg;count+=1.0;}
 }
 return count>0.0?sum/count:vec2(0);
}
void main(){
 ivec2 size=textureSize(uState,0),p=ivec2(gl_FragCoord.xy);
 vec2 world=(vUv-.5)*2.0*uRadius;
 vec4 mask=texelFetch(uObstacles,p,0),oldMask=texelFetch(uPreviousObstacles,p,0);
 if(length(world)>uRadius || mask.r>=.5){nextState=vec2(0);return;}
 vec2 state=fluidState(p);
 // Transfer the departing direct contour warp to the fluid rather than losing it.
 if(uFirstSubstep!=0)state.x-=max(oldMask.a-mask.a,0.0)*(1.0-oldMask.g)*(.014/.65);
 float b=baseAt(p),lap=0.0,motion=0.0;
 ivec2 offsets[4]=ivec2[4](ivec2(1,0),ivec2(-1,0),ivec2(0,1),ivec2(0,-1));
 for(int i=0;i<4;i++){
  ivec2 q=clamp(p+offsets[i],ivec2(0),size-1);
  vec4 m=texelFetch(uObstacles,q,0),old=texelFetch(uPreviousObstacles,q,0);
  float neighbor=fluidState(q).r;
  // Neumann on H=baseline+disturbance: H(ghost)=H(centre).
  // Thus the moving baseline itself drives the reflected wave, even at rest.
  float coverage=smoothstep(.05,.5,m.r);
  float ghost=state.x+(b-baseAt(q))*uResponse;
  lap+=mix(neighbor,ghost,coverage)-state.x;
  // Only silhouette changes outside the union generate motion impulses.
  motion+=max(0.0,m.r-old.r)*(1.0-max(m.g,old.g));
 }
 vec2 uvStep=1.0/vec2(size);
 vec4 mx=texture(uObstacles,vUv+vec2(uvStep.x,0))-texture(uObstacles,vUv-vec2(uvStep.x,0));
 vec4 my=texture(uObstacles,vUv+vec2(0,uvStep.y))-texture(uObstacles,vUv-vec2(0,uvStep.y));
 float movingContour=(abs(mx.a)+abs(my.a))*.5*(1.0-mask.g);
 float velocity=state.y+uSpeedSquared*lap/(uCell*uCell)*uDt;
 // Continuous contour forcing also runs on sleeping props. The smooth field
 // warps the existing ripples; it does not draw an independent ring or glow.
 float agitation=sin(uContactTime*8.0+dot(world,vec2(3.7,2.1)))
                 +.45*sin(uContactTime*13.1+dot(world,vec2(-2.3,4.9)));
 velocity+=mask.a*(1.0-mask.r)*agitation*.18*uResponse*uDt;
 if(uFirstSubstep!=0){
  // Entry is a one-shot velocity impulse on the capsule/prop contour. It must
  // not inherit the slow ramp used to restore persistent obstacle boundaries.
  velocity+=mask.b*uImpulseAmplitude;
  velocity+=clamp(motion*.004+movingContour*uMotionAmplitude*.01,0.0,.025)*uMotionStep;
 }
 if(uPacket!=0)velocity+=exp(-pow((world.x+1.1)/.12,2.0)-pow(world.y/.55,2.0))*.25;
 float rim=smoothstep(uRadius-.35,uRadius,length(world));
 velocity*=exp(-(uDamping+rim*35.0)*uDt);
 float h=state.x+velocity*uDt;
 // Residuals now drive a bounded coordinate warp of the baseline material.
 // Entry displacement is zero; persistent contours provide the response.
 if(uFirstSubstep!=0)h+=mask.b*uImpulseHeight;
 // Bound catastrophic external inputs; normal operation remains well inside these limits.
 nextState=vec2(clamp(h,-.12,.12),clamp(velocity,-3.0,3.0));
}

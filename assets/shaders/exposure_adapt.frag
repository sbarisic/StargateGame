#version 400 core
out vec4 result;uniform sampler2D uTexture,uPrevious;
uniform float uDelta,uDay,uCompensation;
uniform int uAutomatic,uInitialize,uHold,uMeasurementReady;
void main(){
 vec4 old=uInitialize!=0?vec4(0):texelFetch(uPrevious,ivec2(0),0);
 vec2 meter=texelFetch(uTexture,ivec2(0),0).rg;
 bool valid=uMeasurementReady!=0 && meter.y>0.&& !any(isnan(meter)) && !any(isinf(meter));
 bool hold=uHold==2 || (uHold==1 && old.a>=.5);
 // B is automatic exposure without compensation; A indicates a valid history.
 float current=old.b,target=old.g;
 if(valid){
  float key=mix(.045,.18,uDay);
  target=clamp(log2(key)-meter.x/meter.y,mix(-2.,-1.5,uDay),mix(.5,1.5,uDay));
  if(old.a<.5 && !hold)current=target;
  else if(!hold && abs(target-current)>.05){
   float tau=target>current?2.5:.7;
   current=mix(current,target,1.-exp(-uDelta/tau));
  }
 }
 float initialized=max(old.a,valid&&!hold?1.:0.);
 result=vec4((uAutomatic!=0?current:0.)+uCompensation,target,current,initialized);
}

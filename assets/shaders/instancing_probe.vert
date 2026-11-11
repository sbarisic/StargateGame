#version 400 core
layout(location=0) in vec3 aPosition;
flat out int instance;
void main(){instance=gl_InstanceID;gl_Position=vec4(aPosition.x*.5-.5+float(instance),aPosition.y,0,1);}

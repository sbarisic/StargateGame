#version 400 core
flat in int instance;out vec4 fragColor;
void main(){fragColor=instance==0?vec4(1,0,0,1):vec4(0,1,0,1);}

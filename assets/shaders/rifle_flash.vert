#version 400 core
layout(location=0) in vec3 aPosition;
layout(location=1) in vec4 aColor;
layout(location=2) in vec2 aUv;
uniform mat4 uModel, uView, uProjection;
out vec4 vColor;
out vec2 vUv;
void main()
{
    vColor = aColor;
    vUv = aUv;
    gl_Position = uProjection * uView * uModel * vec4(aPosition, 1.0);
}

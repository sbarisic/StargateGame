#version 400 core
uniform sampler2D uTexture;
uniform vec3 uTint;
in vec2 vUv;
out vec4 fragColor;
void main(){
    vec2 pixel=1./vec2(textureSize(uTexture,0));
    float visible=texture(uTexture,vUv).r;
    float interior=visible;
    for(int y=-2;y<=2;y++)for(int x=-2;x<=2;x++)
        interior=min(interior,texture(uTexture,clamp(vUv+vec2(x,y)*pixel,pixel*.5,1.-pixel*.5)).r);
    // Inner outline stays on visible selected surfaces, never through a wall.
    float edge=visible-interior;
    fragColor=vec4(uTint,visible*.24+edge*.65);
}

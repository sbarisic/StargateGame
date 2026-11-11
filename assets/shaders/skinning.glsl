layout(location=4) in vec4 aJoints;
layout(location=5) in vec4 aWeights;
uniform int uSkinning;
uniform mat4 uBones[128];
mat4 skinTransform(){
    if(uSkinning==0)return mat4(1);
    ivec4 j=ivec4(aJoints);
    return uBones[j.x]*aWeights.x+uBones[j.y]*aWeights.y+uBones[j.z]*aWeights.z+uBones[j.w]*aWeights.w;
}

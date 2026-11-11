// Auxiliary opaque outputs. Classification: 0 excluded, 1 solid, 2 leaf, 3 grass.
layout(location=1) out vec4 aoAmbient;
layout(location=2) out vec4 aoNormal;
void writeAmbient(vec3 ambient, vec3 geometricNormal, float materialClass) {
    aoAmbient = vec4(max(ambient, vec3(0)), 0);
    aoNormal = vec4(normalize(geometricNormal) * .5 + .5, materialClass / 3.0);
}
void excludeAmbient() { aoAmbient = vec4(0); aoNormal = vec4(0); }

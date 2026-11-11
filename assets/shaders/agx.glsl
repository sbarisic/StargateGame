// AgX approximation adapted from Three.js tonemapping_pars_fragment.glsl.js.
// Linear sRGB in/out; see docs/FILMIC_EXPOSURE.md for the upstream reference.
/*
The MIT License
Copyright (c) 2010-2026 three.js authors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/
vec3 agxToneMap(vec3 color) {
 const mat3 to2020=mat3(.6274,.0691,.0164, .3293,.9195,.0880, .0433,.0113,.8956);
 const mat3 toSrgb=mat3(1.6605,-.1246,-.0182, -.5876,1.1329,-.1006, -.0728,-.0083,1.1187);
 const mat3 inset=mat3(
  .856627153315983,.137318972929847,.11189821299995,
  .0951212405381588,.761241990602591,.0767994186031903,
  .0482516061458583,.101439036467562,.811302368396859);
 const mat3 outset=mat3(
  1.1271005818144368,-.1413297634984383,-.14132976349843826,
  -.11060664309660323,1.157823702216272,-.11060664309660294,
  -.016493938717834573,-.016493938717834257,1.2519364065950405);
 vec3 x=clamp((log2(max(inset*to2020*color,vec3(1e-10)))+12.47393)/16.5,0.,1.);
 vec3 x2=x*x,x4=x2*x2;
 x=15.5*x4*x2-40.14*x4*x+31.96*x4-6.868*x2*x+.4298*x2+.1191*x-.00232;
 return clamp(toSrgb*pow(max(outset*x,vec3(0)),vec3(2.2)),0.,1.);
}

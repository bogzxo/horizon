// The vertex stage of everything that works on a whole picture, which is the post processing passes, the technique
// that puts a renderer on screen and the scene transitions. One triangle over everything, made up here out of the
// vertex number alone (see ScreenTriangle.Draw, which draws three vertices out of nothing). It reaches well past two
// of the edges, and what sticks out is never drawn. There is no seam down the middle for the pixels along it to be
// shaded twice, the way two triangles would. Include this and the program has its vertex stage, all that is left
// to write is the fragment one, which is handed where it is as texCoords (0 to 1 from the top left corner).

struct ScreenVaryings
{
    float4 position : SV_Position;
    float2 texCoords : TEXCOORD0;
};

[shader("vertex")]
ScreenVaryings vsMain(uint vertexId : SV_VertexID)
{
    // (0, 0), (2, 0), (0, 2) in the picture, so the triangle covers the square from 0 to 1
    ScreenVaryings output;
    output.texCoords = float2((vertexId << 1) & 2, vertexId & 2);
    output.position = float4(output.texCoords * 2.0 - 1.0, 0.0, 1.0);
    return output;
}

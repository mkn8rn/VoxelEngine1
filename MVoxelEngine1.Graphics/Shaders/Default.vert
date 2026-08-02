#version 330 core

// Per-vertex base quad attributes (unit quad in XY plane at z=0)
layout (location = 0) in vec3 aPosition;  // (x,y,0) with x,y in {0,1}

layout (location = 2) in uvec2 iRectangle;
layout (location = 5) in uvec2 tRectangle;

out vec2 rectangleUv;
flat out uint atlasTileIndex;

uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;

uniform vec3 chunkPosition; // chunk base world position
uniform float tilesX;       // atlas tiles horizontally
uniform float tilesY;       // atlas tiles vertically

// Select the instance attribute for this draw call.
// Zero selects attribute 2. A nonzero value selects attribute 5.
uniform float useTransparentList; // 0.0 or 1.0

// Outward orientation mapping
// FRONT(+Z)=5, BACK(-Z)=4, RIGHT(+X)=1, LEFT(-X)=0, TOP(+Y)=3, BOTTOM(-Y)=2
vec3 FacePosition(uint dir, vec2 uv, vec3 o)
{
    vec3 origin; vec3 U; vec3 V;
    if (dir == 5u) { origin = vec3(o.x, o.y, o.z + 1.0); U = vec3(1,0,0); V = vec3(0,1,0); }
    else if (dir == 4u) { origin = vec3(o.x + 1.0, o.y, o.z); U = vec3(-1,0,0); V = vec3(0,1,0); }
    else if (dir == 1u) { origin = vec3(o.x + 1.0, o.y, o.z + 1.0); U = vec3(0,0,-1); V = vec3(0,1,0); }
    else if (dir == 0u) { origin = vec3(o.x, o.y, o.z); U = vec3(0,0,1); V = vec3(0,1,0); }
    else if (dir == 3u) { origin = vec3(o.x, o.y + 1.0, o.z + 1.0); U = vec3(1,0,0); V = vec3(0,0,-1); }
    else {                origin = vec3(o.x, o.y, o.z); U = vec3(1,0,0); V = vec3(0,0,1); }

    return origin + uv.x * U + uv.y * V;
}

void main()
{
    // derive uv from aPosition
    vec2 baseUV = aPosition.xy;

    // Select attribute set based on uniform
    bool useT = (useTransparentList > 0.5);
    uvec2 rectangle = useT ? tRectangle : iRectangle;
    uint packedPosition = rectangle.x;
    uint packedAttributes = rectangle.y;
    uint faceDir = (packedPosition >> 24u) & 7u;
    vec3 instanceOffset = vec3(
        float(packedPosition & 255u),
        float((packedPosition >> 8u) & 255u),
        float((packedPosition >> 16u) & 255u));
    vec2 rectangleExtent = vec2(
        float((packedAttributes & 255u) + 1u),
        float(((packedAttributes >> 8u) & 255u) + 1u));
    rectangleUv = baseUV * rectangleExtent;
    atlasTileIndex = packedAttributes >> 16u;

    vec3 oriented = FacePosition(faceDir, rectangleUv, instanceOffset);
    vec3 worldPosition = oriented + chunkPosition;

    gl_Position = vec4(worldPosition, 1.0) * model * view * projection; // do not change order!

}

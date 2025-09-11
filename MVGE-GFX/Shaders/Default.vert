#version 330 core

// Per-vertex base quad attributes (unit quad in XY plane at z=0)
layout (location = 0) in vec3 aPosition;  // (x,y,0) with x,y in {0,1}

// Per-instance attributes
layout (location = 2) in vec3 iOffset;      // block position (integer coordinates) within chunk
layout (location = 3) in uint iTileIndex;   // tile index in atlas
layout (location = 4) in uint iFaceDir;     // face orientation: 0=L,1=R,2=Bottom,3=Top,4=Back,5=Front

out vec2 texCoord;

uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;

uniform vec3 chunkPosition; // chunk base world position
uniform float tilesX;       // atlas tiles horizontally
uniform float tilesY;       // atlas tiles vertically

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

    uint tx = iTileIndex % uint(tilesX);
    uint ty = iTileIndex / uint(tilesX);
    vec2 tileOffset = vec2(float(tx), float(ty));

    vec3 oriented = FacePosition(iFaceDir, baseUV, iOffset);
    vec3 worldPosition = oriented + chunkPosition;

    gl_Position = vec4(worldPosition, 1.0) * model * view * projection; // do not change order!

    texCoord = (baseUV + tileOffset) / vec2(tilesX, tilesY);
}
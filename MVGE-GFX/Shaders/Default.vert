#version 330 core
layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec2 aTexCoord;

out vec2 texCoord;

// uniform variables
uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;

uniform vec3 chunkPosition;
uniform float tilesX;
uniform float tilesY;

void main() 
{
    vec3 worldPosition = aPosition + chunkPosition;

	gl_Position = projection * view * model * vec4(worldPosition, 1.0);
	
    texCoord = (aTexCoord) / vec2(tilesX, tilesY);
}
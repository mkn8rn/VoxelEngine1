#version 330 core

in vec2 rectangleUv;
flat in uint atlasTileIndex;

out vec4 FragColor;

uniform sampler2D texture0;
uniform float tilesX;
uniform float tilesY;

void main()
{
	uint tileX = atlasTileIndex % uint(tilesX);
	uint tileY = atlasTileIndex / uint(tilesX);
	vec2 tileOffset = vec2(float(tileX), float(tileY));
	vec2 texCoord = (fract(rectangleUv) + tileOffset) / vec2(tilesX, tilesY);
	FragColor = texture(texture0, texCoord);
}

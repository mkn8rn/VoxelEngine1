using MVGE.Graphics.Terrain;
using MVGE.World.Terrain;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using StbImageSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE.Graphics
{
    internal class BlockTextureAtlas
    {
        public int ID;
        public int atlasWidth;
        public int atlasHeight;
        public int tilesX;
        public int tilesY;

        //loading variables
        private int currentX = 0;
        private int currentY = 0;
        private ImageResult fallbackTexture;
        private string fallbackTextureName = "400";
        private ImageResult missingTexture;
        private string missingTextureName = "404";

        //coordinates of all loaded and merged textures
        public static Dictionary<string, Vector2> textureCoordinates = new Dictionary<string, Vector2>();

        //uv coordinates of all faces of all block types
        public static Dictionary<ushort, Dictionary<Faces, ByteVector2>>
            blockTypeUVCoordinates = new Dictionary<ushort, Dictionary<Faces, ByteVector2>>();

        public BlockTextureAtlas()
        {
            Console.WriteLine($"Generating terrain texture atlas.");

            // Count the number of .png files in the base directory
            var baseTextureFiles = Directory.GetFiles(GameManager.settings.assetsBaseBlockTexturesDirectory, "*" + GameManager.settings.textureFileExtension);
            var textureFiles = Directory.GetFiles(GameManager.settings.assetsBlockTexturesDirectory, "*" + GameManager.settings.textureFileExtension);
            int baseTextureCount = baseTextureFiles.Length;
            int textureCount = baseTextureFiles.Length + textureFiles.Length;

            Console.WriteLine($"Textures found: " + textureCount + ", of which base textures are: " + baseTextureCount);

            // Calculate the number of rows and columns needed
            tilesX = (int)Math.Ceiling(Math.Sqrt(textureCount));
            tilesY = (int)Math.Ceiling((double)textureCount / tilesX);

            // Set the atlas width and height based on the number of rows and columns
            atlasWidth = tilesX * GameManager.settings.blockTileWidth;
            atlasHeight = tilesY * GameManager.settings.blockTileHeight;

            // Loading texture into buffer
            ID = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, ID);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, atlasWidth, atlasHeight, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

            // Setting texture parameters
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

            // flip StbImage coords so they match OpenGL coords
            StbImage.stbi_set_flip_vertically_on_load(1);

            // Load the fallback and missing textures
            this.fallbackTexture = ImageResult.FromStream(File.OpenRead(GameManager.settings.assetsBaseBlockTexturesDirectory + fallbackTextureName + GameManager.settings.textureFileExtension),
                ColorComponents.RedGreenBlueAlpha);
            this.missingTexture = ImageResult.FromStream(File.OpenRead(GameManager.settings.assetsBaseBlockTexturesDirectory + missingTextureName + GameManager.settings.textureFileExtension),
                ColorComponents.RedGreenBlueAlpha);

            // Load textures into the Atlas
            Console.WriteLine($"Loading base textures to atlas.");
            LoadTextureIntoAtlas(baseTextureFiles);
            Console.WriteLine($"Loading other textures to atlas.");
            LoadTextureIntoAtlas(textureFiles);

            InitializeBlockTypeUVCoordinates();
            // Map texture coordinates to block types and faces
            MapTextureCoordinates();

            int emptyTiles = tilesX * tilesY - textureCount;

            Console.WriteLine($"Atlas finished generating.");
            Console.WriteLine($"Atlas total width: {atlasWidth}");
            Console.WriteLine($"Atlas total height: {atlasHeight}");
            Console.WriteLine($"Number of textures loaded: {textureCount}");
            Console.WriteLine($"Number of tiles in X direction: {tilesX}");
            Console.WriteLine($"Number of tiles in Y direction: {tilesY}");
            Console.WriteLine($"Number of empty tiles: {emptyTiles}/{tilesY * tilesX}");

            GL.BindTexture(TextureTarget.Texture2D, 0);

            var error = GL.GetError();
            if (error != ErrorCode.NoError)
            {
                Console.WriteLine($"OpenGL Error: {error}");
            }
        }

        private void LoadTextureIntoAtlas(string[] texturesToLoad)
        {
            foreach (var texture in texturesToLoad)
            {
                var textureName = Path.GetFileNameWithoutExtension(texture);

                var loadedTexture = ImageResult.FromStream(File.OpenRead(texture), ColorComponents.RedGreenBlueAlpha);

                if (loadedTexture.Width != GameManager.settings.blockTileWidth
                    || loadedTexture.Height != GameManager.settings.blockTileHeight
                    || loadedTexture.Data == null
                    || loadedTexture.Data.Length == 0)
                {
                    // Load the fallback texture instead
                    Console.WriteLine($"Fallback texture applied for: {textureName}");
                    loadedTexture = fallbackTexture;
                }

                if (currentX + GameManager.settings.blockTileWidth > atlasWidth)
                {
                    currentX = 0;
                    currentY += GameManager.settings.blockTileHeight;
                }

                if (currentY + GameManager.settings.blockTileHeight > atlasHeight)
                {
                    throw new Exception("Texture atlas is too small to fit all textures.");
                }

                GL.TexSubImage2D(TextureTarget.Texture2D, 0, currentX, currentY,
                    GameManager.settings.blockTileWidth, GameManager.settings.blockTileHeight,
                    PixelFormat.Rgba, PixelType.UnsignedByte, loadedTexture.Data);

                var floatCoordsX = (float)currentX / (float)GameManager.settings.blockTileWidth;
                var floatCoordsY = (float)currentY / (float)GameManager.settings.blockTileHeight;

                textureCoordinates[textureName] = new Vector2(floatCoordsX, floatCoordsY);

                Console.WriteLine($"Loaded texture: {textureName} - Position: ({currentX}, {currentY}), Width: {loadedTexture.Width}, Height: {loadedTexture.Height}, " +
                  $"UV Coordinates: ({floatCoordsX}, {floatCoordsY})");

                currentX += GameManager.settings.blockTileWidth;
            }
        }

        private void InitializeBlockTypeUVCoordinates()
        {
            ushort count = 0;
            foreach (string blockType in TerrainDataLoader.allBlockTypes)
            {
                blockTypeUVCoordinates[count] = new Dictionary<Faces, ByteVector2>();

                foreach (var face in Enum.GetValues(typeof(Faces)).Cast<Faces>())
                {
                    blockTypeUVCoordinates[count][face] = new ByteVector2();
                }

                count++;
            }
        }

        private void MapTextureCoordinates()
        {
            Console.WriteLine($"Mapping texture coordinates.");

            ushort count = 0;
            foreach (string blockType in TerrainDataLoader.allBlockTypesByBaseType.Keys)
            {
                string textureName = blockType.ToString();

                if (textureCoordinates.ContainsKey(textureName))
                {
                    Vector2 textureCoordinates = BlockTextureAtlas.textureCoordinates[textureName];
                    ByteVector2 textureByteCoordinates = new ByteVector2 { x = (byte)textureCoordinates.X, y = (byte)textureCoordinates.Y };
                    foreach (Faces face in Enum.GetValues(typeof(Faces)).Cast<Faces>())
                    {
                        Console.WriteLine($"{blockType}, face: {face} coords set to: ({textureCoordinates.X},{textureCoordinates.Y})");
                        blockTypeUVCoordinates[count][face] = textureByteCoordinates;
                    }
                }
                else
                {
                    Console.WriteLine($"Missing texture for: {textureName}. Setting texture coords to fallback texture.");
                    Vector2 textureCoordinates = BlockTextureAtlas.textureCoordinates[missingTextureName];
                    ByteVector2 textureByteCoordinates = new ByteVector2 { x = (byte)textureCoordinates.X, y = (byte)textureCoordinates.Y };
                    foreach (Faces face in Enum.GetValues(typeof(Faces)).Cast<Faces>())
                    {
                        blockTypeUVCoordinates[count][face] = textureByteCoordinates;
                    }
                }

                count++;
            }
        }

        public List<ByteVector2> GetBlockUVs(ushort blockType, Faces face)
        {
            List<ByteVector2> faceData = new List<ByteVector2>();

            Dictionary<Faces, ByteVector2> blockCoords = blockTypeUVCoordinates[blockType];
            ByteVector2 faceCoords = blockCoords[face];

            faceData = new List<ByteVector2>()
            {
                new ByteVector2{ x = (byte)(faceCoords.x + 1), y = (byte)(faceCoords.y + 1) },
                new ByteVector2{ x = (byte)(faceCoords.x), y = (byte)(faceCoords.y + 1) },
                new ByteVector2{ x = (byte)(faceCoords.x), y = (byte)(faceCoords.y) },
                new ByteVector2{ x = (byte)(faceCoords.x + 1), y = (byte)(faceCoords.y) },
            };

            return faceData;
        }

        public void Bind()
        {
            GL.BindTexture(TextureTarget.Texture2D, ID);
        }

        public void Unbind()
        {
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }
    }
}

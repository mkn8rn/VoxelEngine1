using MVGE_GFX.Models;
using MVGE_INF.Loaders;
using MVGE_INF.Managers;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using StbImageSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MVGE_GFX.Textures
{
    public class BlockTextureAtlas
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

        // --- Async IO preload support ---
        private struct RawImage
        {
            public string Name;
            public byte[] Data; // RGBA
            public int Width;
            public int Height;
        }
        private static Task<List<RawImage>> preloadTask; // file IO + decode (no GL)
        private static readonly object preloadLock = new();
        private static volatile bool atlasBuilt = false;
        private static BlockTextureAtlas instance; // lazy-built when GL upload occurs

        public static void BeginAsyncIOPreload()
        {
            if (preloadTask != null) return;
            lock (preloadLock)
            {
                if (preloadTask != null) return;
                preloadTask = Task.Run(() =>
                {
                    var list = new List<RawImage>(256);
                    try
                    {
                        // flip StbImage coords so they match OpenGL coords
                        StbImage.stbi_set_flip_vertically_on_load(1);
                        string baseDir = GameManager.settings.assetsBaseBlockTexturesDirectory;
                        string extraDir = GameManager.settings.assetsBlockTexturesDirectory;
                        string ext = GameManager.settings.textureFileExtension;
                        var baseFiles = Directory.GetFiles(baseDir, "*" + ext);
                        var extraFiles = Directory.GetFiles(extraDir, "*" + ext);
                        foreach (var f in baseFiles.Concat(extraFiles))
                        {
                            try
                            {
                                using var fs = File.OpenRead(f);
                                var img = ImageResult.FromStream(fs, ColorComponents.RedGreenBlueAlpha);
                                list.Add(new RawImage
                                {
                                    Name = Path.GetFileNameWithoutExtension(f),
                                    Data = img.Data,
                                    Width = img.Width,
                                    Height = img.Height
                                });
                            }
                            catch { /* ignore single file errors */ }
                        }
                    }
                    catch { }
                    return list;
                });
            }
        }

        public static BlockTextureAtlas GetOrCreate()
        {
            if (atlasBuilt) return instance;
            lock (preloadLock)
            {
                if (atlasBuilt) return instance;
                // Ensure preload started
                BeginAsyncIOPreload();
                List<RawImage> images = null;
                try { images = preloadTask?.GetAwaiter().GetResult(); } catch { images = new List<RawImage>(); }
                instance = new BlockTextureAtlas(images);
                atlasBuilt = true;
                return instance;
            }
        }

        // Private ctor for lazy async pathway
        private BlockTextureAtlas(List<RawImage> preloaded)
        {
            Console.WriteLine($"Generating terrain texture atlas (async preloaded={preloaded?.Count}).");
            string baseDir = GameManager.settings.assetsBaseBlockTexturesDirectory;
            string ext = GameManager.settings.textureFileExtension;
            StbImage.stbi_set_flip_vertically_on_load(1);
            fallbackTexture = ImageResult.FromStream(File.OpenRead(Path.Combine(baseDir, fallbackTextureName + ext)), ColorComponents.RedGreenBlueAlpha);
            missingTexture = ImageResult.FromStream(File.OpenRead(Path.Combine(baseDir, missingTextureName + ext)), ColorComponents.RedGreenBlueAlpha);

            if (preloaded == null || preloaded.Count == 0)
            {
                // Re-execute blocking path inline (same as original ctor without duplication)
                var baseTextureFiles = Directory.GetFiles(GameManager.settings.assetsBaseBlockTexturesDirectory, "*" + ext);
                var textureFiles = Directory.GetFiles(GameManager.settings.assetsBlockTexturesDirectory, "*" + ext);
                int textureCountAll = baseTextureFiles.Length + textureFiles.Length;
                tilesX = (int)Math.Ceiling(Math.Sqrt(textureCountAll));
                tilesY = (int)Math.Ceiling((double)textureCountAll / tilesX);
                atlasWidth = tilesX * GameManager.settings.blockTileWidth;
                atlasHeight = tilesY * GameManager.settings.blockTileHeight;
                ID = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, ID);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, atlasWidth, atlasHeight, 0, PixelFormat.Rgba, PixelType.UnsignedByte, nint.Zero);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                currentX = 0; currentY = 0; textureCoordinates.Clear();
                LoadTextureIntoAtlas(baseTextureFiles);
                LoadTextureIntoAtlas(textureFiles);
                InitializeBlockTypeUVCoordinates();
                MapTextureCoordinates();
                GL.BindTexture(TextureTarget.Texture2D, 0);
                return;
            }

            // Build tile set (include fallback/missing if not already)
            var byName = new Dictionary<string, RawImage>(StringComparer.OrdinalIgnoreCase);
            foreach (var ri in preloaded)
            {
                if (ri.Width != GameManager.settings.blockTileWidth || ri.Height != GameManager.settings.blockTileHeight || ri.Data == null)
                    continue; // skip invalid; we will supply fallback later if requested
                byName[ri.Name] = ri;
            }
            if (!byName.ContainsKey(fallbackTextureName))
            {
                byName[fallbackTextureName] = new RawImage { Name = fallbackTextureName, Data = fallbackTexture.Data, Width = fallbackTexture.Width, Height = fallbackTexture.Height };
            }
            if (!byName.ContainsKey(missingTextureName))
            {
                byName[missingTextureName] = new RawImage { Name = missingTextureName, Data = missingTexture.Data, Width = missingTexture.Width, Height = missingTexture.Height };
            }

            // Determine final list order (stable order for determinism)
            var textureNames = byName.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
            int textureCount = textureNames.Count;
            tilesX = (int)Math.Ceiling(Math.Sqrt(textureCount));
            tilesY = (int)Math.Ceiling((double)textureCount / tilesX);
            atlasWidth = tilesX * GameManager.settings.blockTileWidth;
            atlasHeight = tilesY * GameManager.settings.blockTileHeight;

            ID = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, ID);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, atlasWidth, atlasHeight, 0, PixelFormat.Rgba, PixelType.UnsignedByte, nint.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

            currentX = 0; currentY = 0; textureCoordinates.Clear();
            foreach (var name in textureNames)
            {
                var ri = byName[name];
                if (currentX + ri.Width > atlasWidth)
                {
                    currentX = 0; currentY += GameManager.settings.blockTileHeight;
                }
                if (currentY + ri.Height > atlasHeight) break; // overflow safety
                int tileX = currentX / GameManager.settings.blockTileWidth;
                int tileY = currentY / GameManager.settings.blockTileHeight;
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, currentX, currentY,
                    ri.Width, ri.Height, PixelFormat.Rgba, PixelType.UnsignedByte, ri.Data);
                float floatCoordsX = currentX / (float)GameManager.settings.blockTileWidth;
                float floatCoordsY = currentY / (float)GameManager.settings.blockTileHeight;
                textureCoordinates[name] = new Vector2(floatCoordsX, floatCoordsY);
                Console.WriteLine($"[Atlas] Loaded texture '{name}' at tile ({tileX},{tileY}) pixels ({currentX},{currentY}) UV base ({floatCoordsX},{floatCoordsY})");
                currentX += ri.Width;
            }

            InitializeBlockTypeUVCoordinates();
            MapTextureCoordinates();
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        // Original blocking constructor remains for legacy direct call paths
        public BlockTextureAtlas()
        {
            Console.WriteLine($"Generating terrain texture atlas.");

            var baseTextureFiles = Directory.GetFiles(GameManager.settings.assetsBaseBlockTexturesDirectory, "*" + GameManager.settings.textureFileExtension);
            var textureFiles = Directory.GetFiles(GameManager.settings.assetsBlockTexturesDirectory, "*" + GameManager.settings.textureFileExtension);
            int baseTextureCount = baseTextureFiles.Length;
            int textureCount = baseTextureFiles.Length + textureFiles.Length;

            Console.WriteLine($"Textures found: " + textureCount + ", of which base textures are: " + baseTextureCount);

            tilesX = (int)Math.Ceiling(Math.Sqrt(textureCount));
            tilesY = (int)Math.Ceiling((double)textureCount / tilesX);
            atlasWidth = tilesX * GameManager.settings.blockTileWidth;
            atlasHeight = tilesY * GameManager.settings.blockTileHeight;

            ID = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, ID);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, atlasWidth, atlasHeight, 0, PixelFormat.Rgba, PixelType.UnsignedByte, nint.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

            StbImage.stbi_set_flip_vertically_on_load(1);
            fallbackTexture = ImageResult.FromStream(File.OpenRead(GameManager.settings.assetsBaseBlockTexturesDirectory + fallbackTextureName + GameManager.settings.textureFileExtension),
                ColorComponents.RedGreenBlueAlpha);
            missingTexture = ImageResult.FromStream(File.OpenRead(GameManager.settings.assetsBaseBlockTexturesDirectory + missingTextureName + GameManager.settings.textureFileExtension),
                ColorComponents.RedGreenBlueAlpha);

            Console.WriteLine($"Loading base textures to atlas.");
            LoadTextureIntoAtlas(baseTextureFiles);
            Console.WriteLine($"Loading other textures to atlas.");
            LoadTextureIntoAtlas(textureFiles);

            InitializeBlockTypeUVCoordinates();
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
                int tileX = currentX / GameManager.settings.blockTileWidth;
                int tileY = currentY / GameManager.settings.blockTileHeight;
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, currentX, currentY,
                    GameManager.settings.blockTileWidth, GameManager.settings.blockTileHeight,
                    PixelFormat.Rgba, PixelType.UnsignedByte, loadedTexture.Data);
                var floatCoordsX = currentX / (float)GameManager.settings.blockTileWidth;
                var floatCoordsY = currentY / (float)GameManager.settings.blockTileHeight;
                textureCoordinates[textureName] = new Vector2(floatCoordsX, floatCoordsY);
                Console.WriteLine($"[Atlas] Loaded texture '{textureName}' at tile ({tileX},{tileY}) pixels ({currentX},{currentY}) UV base ({floatCoordsX},{floatCoordsY})");
                currentX += GameManager.settings.blockTileWidth;
            }
        }

        private void InitializeBlockTypeUVCoordinates()
        {
            blockTypeUVCoordinates.Clear();
            // Initialize only existing block IDs to avoid mismatches; MapTextureCoordinates fills values
            foreach (var bt in TerrainLoader.allBlockTypeObjects)
            {
                var dict = new Dictionary<Faces, ByteVector2>();
                foreach (Faces f in Enum.GetValues(typeof(Faces)))
                    dict[f] = new ByteVector2();
                blockTypeUVCoordinates[bt.ID] = dict;
            }
        }

        private void MapTextureCoordinates()
        {
            Console.WriteLine($"Mapping texture coordinates.");

            foreach (var bt in TerrainLoader.allBlockTypeObjects)
            {
                if (!blockTypeUVCoordinates.TryGetValue(bt.ID, out var faceDict))
                {
                    // Safety: initialize if missing
                    faceDict = new Dictionary<Faces, ByteVector2>();
                    foreach (var faceInit in Enum.GetValues(typeof(Faces)).Cast<Faces>()) faceDict[faceInit] = new ByteVector2();
                    blockTypeUVCoordinates[bt.ID] = faceDict;
                }

                ByteVector2 Resolve(string texName, Faces face)
                {
                    if (string.IsNullOrWhiteSpace(texName) || !textureCoordinates.TryGetValue(texName, out var vec))
                    {
                        var missVec = textureCoordinates[missingTextureName];
                        Console.WriteLine($"[Atlas] Block '{bt.Name}' face {face} texture '{texName ?? "<null>"}' NOT FOUND -> using '{missingTextureName}' at tile ({missVec.X},{missVec.Y})");
                        return new ByteVector2 { x = (byte)missVec.X, y = (byte)missVec.Y };
                    }
                    else
                    {
                        Console.WriteLine($"[Atlas] Block '{bt.Name}' face {face} mapped texture '{texName}' at tile ({vec.X},{vec.Y})");
                        return new ByteVector2 { x = (byte)vec.X, y = (byte)vec.Y };
                    }
                }

                faceDict[Faces.TOP] = Resolve(bt.TextureFaceTop, Faces.TOP);
                faceDict[Faces.BOTTOM] = Resolve(bt.TextureFaceBottom, Faces.BOTTOM);
                faceDict[Faces.FRONT] = Resolve(bt.TextureFaceFront, Faces.FRONT);
                faceDict[Faces.BACK] = Resolve(bt.TextureFaceBack, Faces.BACK);
                faceDict[Faces.LEFT] = Resolve(bt.TextureFaceLeft, Faces.LEFT);
                faceDict[Faces.RIGHT] = Resolve(bt.TextureFaceRight, Faces.RIGHT);
            }
        }

        public List<ByteVector2> GetBlockUVs(ushort blockType, Faces face)
        {
            if (!blockTypeUVCoordinates.TryGetValue(blockType, out var blockCoords))
            {
                // Lazy-create an entry mapping all faces to missing texture to avoid KeyNotFound
                Console.WriteLine($"[UV] Warning: Block ID {blockType} missing from atlas; using missing texture.");
                if (!textureCoordinates.TryGetValue(missingTextureName, out var miss))
                    miss = Vector2.Zero;
                var missByte = new ByteVector2 { x = (byte)miss.X, y = (byte)miss.Y };
                blockCoords = new Dictionary<Faces, ByteVector2>();
                foreach (Faces f in Enum.GetValues(typeof(Faces))) blockCoords[f] = missByte;
                blockTypeUVCoordinates[blockType] = blockCoords;
            }
            var faceCoords = blockCoords[face];

            return new List<ByteVector2>
            {
                new ByteVector2{ x = (byte)(faceCoords.x + 1), y = (byte)(faceCoords.y + 1) },
                new ByteVector2{ x = faceCoords.x, y = (byte)(faceCoords.y + 1) },
                new ByteVector2{ x = faceCoords.x, y = faceCoords.y },
                new ByteVector2{ x = (byte)(faceCoords.x + 1), y = faceCoords.y },
            };
        }

        public void Bind() => GL.BindTexture(TextureTarget.Texture2D, ID);
        public void Unbind() => GL.BindTexture(TextureTarget.Texture2D, 0);
    }
}

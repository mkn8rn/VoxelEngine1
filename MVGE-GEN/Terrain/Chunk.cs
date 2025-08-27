using MVGE_GEN.Utils;
using MVGE_GFX;
using MVGE_GFX.Terrain;
using MVGE_INF.Managers;
using MVGE_Tools.Noise;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Buffers;
using MVGE_INF.Models.Terrain;
using MVGE_INF.Generation.Models;
using MVGE_INF.Models.Generation;

namespace MVGE_GEN.Terrain
{
    public partial class Chunk
    {
        public Vector3 position { get; set; }
        public ChunkRender chunkRender;
        public ChunkData chunkData;
        public string saveDirectory;
        public long generationSeed;

        public ChunkSection[,,] sections;
        public int sectionsX;
        public int sectionsY;
        public int sectionsZ;

        public float[,] precomputedHeightmap;
        public static readonly Dictionary<long, OpenSimplexNoise> noiseCache = new();

        public const int SECTION_SHIFT = 4;
        public const int SECTION_MASK = 0xF;

        private readonly int dimX;
        private readonly int dimY;
        private readonly int dimZ;
        private const ushort EMPTY = (ushort)BaseBlockType.Empty;

        private Biome biome;

        // Occupancy flags
        public bool IsEmpty { get; private set; }
        public bool HasAnyBoundarySolid { get; private set; }
        [Flags]
        public enum OcclusionClass : byte
        {
            None = 0,
            FullyBuried = 1 << 0,
            NeighborBuried = 1 << 1
        }

        public OcclusionClass OcclusionStatus { get; private set; } = OcclusionClass.None;

        public bool FullyBuried { get; private set; } // heightmap burial classification
        public bool BuriedByNeighbors { get; internal set; }

        internal void SetFullyBuried()
        {
            if (!FullyBuried)
            {
                FullyBuried = true;
                OcclusionStatus |= OcclusionClass.FullyBuried;
            }
        }
        internal void SetNeighborBuried()
        {
            if (!BuriedByNeighbors)
            {
                BuriedByNeighbors = true;
                OcclusionStatus |= OcclusionClass.NeighborBuried;
            }
        }

        // Fast path: entire chunk volume is guaranteed all air (lies completely above max surface height for every column)
        public bool AllAirChunk { get; private set; }
        // Fast path: entire chunk volume is uniform stone (no soil/air inside)
        public bool AllStoneChunk { get; private set; }
        // Fast path: entire chunk volume is uniform soil (no stone/air inside)
        public bool AllSoilChunk { get; private set; }
        // Post-replacement fast path: entire chunk still a single non-air block (originally stone or soil, may have been transformed by replacement rules)
        public bool AllOneBlockChunk { get; private set; }
        public ushort AllOneBlockBlockId { get; private set; } // The uniform non-air block id for AllOneBlockChunk

        // per-face full solidity flags (all boundary voxels on that face are non-empty)
        // Naming: NegX = x==0 face ("left"), PosX = x==dimX-1 ("right"), etc.
        public bool FaceSolidNegX { get; private set; }
        public bool FaceSolidPosX { get; private set; }
        public bool FaceSolidNegY { get; private set; }
        public bool FaceSolidPosY { get; private set; }
        public bool FaceSolidNegZ { get; private set; }
        public bool FaceSolidPosZ { get; private set; }

        // Neighbor opposing face solidity flags (populated by WorldResources before BuildRender)
        // These reflect the solidity of the neighbor face that touches this chunk.
        public bool NeighborNegXFaceSolidPosX { get; internal set; } // neighbor at -X, its +X face solid
        public bool NeighborPosXFaceSolidNegX { get; internal set; }
        public bool NeighborNegYFaceSolidPosY { get; internal set; }
        public bool NeighborPosYFaceSolidNegY { get; internal set; }
        public bool NeighborNegZFaceSolidPosZ { get; internal set; }
        public bool NeighborPosZFaceSolidNegZ { get; internal set; }

        private bool candidateFullyBuried; // heightmap suggested burial; confirmed after face solidity scan

        public Chunk(Vector3 chunkPosition, long seed, string chunkDataDirectory, float[,] precomputedHeightmap = null)
        {
            position = chunkPosition;
            saveDirectory = chunkDataDirectory;
            generationSeed = seed;
            this.precomputedHeightmap = precomputedHeightmap;

            dimX = GameManager.settings.chunkMaxX;
            dimY = GameManager.settings.chunkMaxY;
            dimZ = GameManager.settings.chunkMaxZ;

            chunkData = new ChunkData
            {
                x = position.X,
                y = position.Y,
                z = position.Z,
                temperature = 0,
                humidity = 0
            };

            // Select biome deterministically
            biome = BiomeManager.SelectBiomeForChunk(seed, (int)position.X, (int)position.Z);

            InitializeSectionGrid();
            InitializeChunkData(); // implemented in ChunkGenerator partial

            // After generation compute per-face solidity once (all writes were generation-only bulk writes)
            if (!AllAirChunk && !AllStoneChunk && !AllSoilChunk) // nothing to scan for pure air or uniform stone/soil fast-path (stone/soil sets flags directly)
                ComputeAllFaceSolidity();

            // Confirm burial only if all six faces ended up solid
            if (candidateFullyBuried && FaceSolidNegX && FaceSolidPosX && FaceSolidNegY && FaceSolidPosY && FaceSolidNegZ && FaceSolidPosZ)
            {
                SetFullyBuried();
            }

            BuildAllBoundaryPlanesInitial();
        }

        public void InitializeSectionGrid()
        {
            int S = ChunkSection.SECTION_SIZE;
            if (dimX % S != 0 || dimY % S != 0 || dimZ % S != 0)
            {
                throw new InvalidOperationException(
                    "Chunk dimensions must be multiples of section size: " + ChunkSection.SECTION_SIZE);
            }
            sectionsX = dimX / S;
            sectionsY = dimY / S;
            sectionsZ = dimZ / S;
            sections = new ChunkSection[sectionsX, sectionsY, sectionsZ];
        }

        public void InitializeChunkData() => GenerateInitialChunkData();

        // NOTE: InitializeChunkData & all generation helpers are in ChunkGenerator partial file.
        public void Render(ShaderProgram shader) => chunkRender?.Render(shader);

        public ushort GenerateInitialBlockData(int lx, int ly, int lz, int columnHeight)
        {
            int currentHeight = (int)(position.Y + ly);
            ushort type = EMPTY;
            if (currentHeight <= columnHeight || currentHeight == 0)
                type = (ushort)BaseBlockType.Stone;
            int soilModifier = 100 - currentHeight / 2;
            if (type == EMPTY && currentHeight < columnHeight + soilModifier)
                type = (ushort)BaseBlockType.Soil;
            return type;
        }

        public float[,] GenerateHeightMap(long seed) => GenerateHeightMap(seed, (int)position.X, (int)position.Z);

        public static float[,] GenerateHeightMap(long seed, int chunkBaseX, int chunkBaseZ)
        {
            if (!noiseCache.TryGetValue(seed, out var noise))
            {
                noise = new OpenSimplexNoise(seed);
                noiseCache[seed] = noise;
            }

            int maxX = GameManager.settings.chunkMaxX;
            int maxZ = GameManager.settings.chunkMaxZ;
            float[,] heightmap = new float[maxX, maxZ];
            float scale = 0.05f;
            float minHeight = 1f;
            float maxHeight = 1000f;

            for (int x = 0; x < maxX; x++)
            {
                for (int z = 0; z < maxZ; z++)
                {
                    float noiseValue = (float)noise.Evaluate((x + chunkBaseX) * scale, (z + chunkBaseZ) * scale);
                    float normalizedValue = noiseValue * 0.5f + 0.5f;
                    heightmap[x, z] = normalizedValue * (maxHeight - minHeight) + minHeight;
                }
            }
            return heightmap;
        }

        public ChunkSection GetOrCreateSection(int sx, int sy, int sz)
        {
            var sec = sections[sx, sy, sz];
            if (sec == null)
            {
                sec = new ChunkSection();
                sections[sx, sy, sz] = sec;
            }
            return sec;
        }

        public void LocalToSection(int lx, int ly, int lz,
            out int sx, out int sy, out int sz,
            out int ox, out int oy, out int oz)
        {
            sx = lx >> SECTION_SHIFT;
            sy = ly >> SECTION_SHIFT;
            sz = lz >> SECTION_SHIFT;
            ox = lx & SECTION_MASK;
            oy = ly & SECTION_MASK;
            oz = lz & SECTION_MASK;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort GetBlockLocal(int lx, int ly, int lz)
        {
            if ((uint)lx >= (uint)dimX || (uint)ly >= (uint)dimY || (uint)lz >= (uint)dimZ)
                return EMPTY;

            int sx = lx >> SECTION_SHIFT;
            int sy = ly >> SECTION_SHIFT;
            int sz = lz >> SECTION_SHIFT;
            var sec = sections[sx, sy, sz];
            if (sec == null) return EMPTY;
            int ox = lx & SECTION_MASK;
            int oy = ly & SECTION_MASK;
            int oz = lz & SECTION_MASK;
            return SectionUtils.GetBlock(sec, ox, oy, oz);
        }

        public void SetBlockLocal(int lx, int ly, int lz, ushort blockId)
        {
            if ((uint)lx >= (uint)dimX || (uint)ly >= (uint)dimY || (uint)lz >= (uint)dimZ)
                return;

            LocalToSection(lx, ly, lz, out int sx, out int sy, out int sz, out int ox, out int oy, out int oz);
            var sec = GetOrCreateSection(sx, sy, sz);
            SectionUtils.SetBlock(sec, ox, oy, oz, blockId);

            // Update face solidity if we touched a boundary cell (cheap plane scan only for affected faces)
            if (lx == 0) FaceSolidNegX = ScanFaceSolidNegX();
            if (lx == dimX - 1) FaceSolidPosX = ScanFaceSolidPosX();
            if (lz == 0) FaceSolidNegZ = ScanFaceSolidNegZ();
            if (lz == dimZ - 1) FaceSolidPosZ = ScanFaceSolidPosZ();
            if (ly == 0) FaceSolidNegY = ScanFaceSolidNegY();
            if (ly == dimY - 1) FaceSolidPosY = ScanFaceSolidPosY();

            if (lx == 0 || lx == dimX - 1 || ly == 0 || ly == dimY - 1 || lz == 0 || lz == dimZ - 1)
            {
                UpdateBoundaryPlaneBit(lx, ly, lz, blockId);
            }
        }

        // Per-face solidity helpers
        private void ComputeAllFaceSolidity()
        {
            FaceSolidNegX = ScanFaceSolidNegX();
            FaceSolidPosX = ScanFaceSolidPosX();
            FaceSolidNegY = ScanFaceSolidNegY();
            FaceSolidPosY = ScanFaceSolidPosY();
            FaceSolidNegZ = ScanFaceSolidNegZ();
            FaceSolidPosZ = ScanFaceSolidPosZ();
        }

        private bool ScanFaceSolidNegX()
        {
            for (int y = 0; y < dimY; y++)
                for (int z = 0; z < dimZ; z++)
                    if (GetBlockLocal(0, y, z) == EMPTY) return false;
            return true;
        }
        private bool ScanFaceSolidPosX()
        {
            int x = dimX - 1;
            for (int y = 0; y < dimY; y++)
                for (int z = 0; z < dimZ; z++)
                    if (GetBlockLocal(x, y, z) == EMPTY) return false;
            return true;
        }
        private bool ScanFaceSolidNegY()
        {
            for (int x = 0; x < dimX; x++)
                for (int z = 0; z < dimZ; z++)
                    if (GetBlockLocal(x, 0, z) == EMPTY) return false;
            return true;
        }
        private bool ScanFaceSolidPosY()
        {
            int y = dimY - 1;
            for (int x = 0; x < dimX; x++)
                for (int z = 0; z < dimZ; z++)
                    if (GetBlockLocal(x, y, z) == EMPTY) return false;
            return true;
        }
        private bool ScanFaceSolidNegZ()
        {
            for (int x = 0; x < dimX; x++)
                for (int y = 0; y < dimY; y++)
                    if (GetBlockLocal(x, y, 0) == EMPTY) return false;
            return true;
        }
        private bool ScanFaceSolidPosZ()
        {
            int z = dimZ - 1;
            for (int x = 0; x < dimX; x++)
                for (int y = 0; y < dimY; y++)
                    if (GetBlockLocal(x, y, z) == EMPTY) return false;
            return true;
        }

    }
}

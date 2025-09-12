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
using MVGE_INF.Models.Generation.Biomes;
using MVGE_INF.Loaders;

namespace MVGE_GEN.Terrain
{
    public partial class Chunk
    {
        // Optional uniform override supplied by batch classification to skip normal span derivation path.
        internal enum UniformOverride { None = 0, AllAir, AllStone, AllSoil }
        private readonly UniformOverride _uniformOverride;

        public Vector3 position { get; set; }
        public ChunkRender chunkRender;
        public ChunkData chunkData;
        public string saveDirectory;
        public long generationSeed;

        public ChunkSection[,,] sections;
        public int sectionsX;
        public int sectionsY;
        public int sectionsZ;

        public const int SECTION_SHIFT = 4;
        public const int SECTION_MASK = 0xF;

        private readonly int dimX;
        private readonly int dimY;
        private readonly int dimZ;
        private const ushort EMPTY = (ushort)BaseBlockType.Empty;

        private Biome biome;

        // Occupancy flags
        public bool HasAnyBoundarySolid { get; internal set; }
        [Flags]
        public enum OcclusionClass : byte
        {
            None = 0,
            FullyBuried = 1 << 0,
            NeighborBuried = 1 << 1
        }

        public OcclusionClass OcclusionStatus { get; internal set; } = OcclusionClass.None;

        public bool FullyBuried { get; internal set; } // heightmap burial classification
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
        public bool AllAirChunk { get; internal set; }
        // Fast path: entire chunk volume is uniform stone (no soil/air inside)
        public bool AllStoneChunk { get; internal set; }
        // Fast path: entire chunk volume is uniform soil (no stone/air inside)
        public bool AllSoilChunk { get; internal set; }
        // Post-replacement fast path: entire chunk still a single non-air block (originally stone or soil, may have been transformed by replacement rules)
        public bool AllOneBlockChunk { get; internal set; }
        public ushort AllOneBlockBlockId { get; internal set; } // The uniform non-air block id for AllOneBlockChunk

        // per-face full solidity flags (all boundary voxels on that face are non-empty & opaque).
        // Naming: NegX = x==0 face ("left"), PosX = x==dimX-1 ("right"), etc.
        public bool FaceSolidNegX { get; internal set; }
        public bool FaceSolidPosX { get; internal set; }
        public bool FaceSolidNegY { get; internal set; }
        public bool FaceSolidPosY { get; internal set; }
        public bool FaceSolidNegZ { get; internal set; }
        public bool FaceSolidPosZ { get; internal set; }

        // Neighbor opposing face solidity flags (populated by WorldResources before BuildRender)
        // These reflect the solidity of the neighbor face that touches this chunk.
        public bool NeighborNegXFaceSolidPosX { get; internal set; } // neighbor at -X, its +X face solid
        public bool NeighborPosXFaceSolidNegX { get; internal set; }
        public bool NeighborNegYFaceSolidPosY { get; internal set; }
        public bool NeighborPosYFaceSolidNegY { get; internal set; }
        public bool NeighborNegZFaceSolidPosZ { get; internal set; }
        public bool NeighborPosZFaceSolidNegZ { get; internal set; }

        private bool candidateFullyBuried; // heightmap suggested burial; confirmed after face solidity scan

        // autogenerate = true for new chunks, false when loading from disk
        internal Chunk(Vector3 chunkPosition,
                       long seed,
                       string chunkDataDirectory,
                       bool autoGenerate,
                       UniformOverride uniformOverride = UniformOverride.None,
                       BlockColumnProfile[] columnSpanMap = null)
        {
            position = chunkPosition;
            saveDirectory = chunkDataDirectory;
            generationSeed = seed;
            _uniformOverride = uniformOverride;

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

            // Select biome deterministically (needed for both generated & loaded chunks)
            biome = BiomeManager.SelectBiomeForChunk(seed, (int)position.X, (int)position.Z);

            InitializeSectionGrid();
            if (autoGenerate)
            {
                InitializeChunkData(columnSpanMap); // triggers GenerateInitialChunkData or uniform shortcut
            }
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

        internal void InitializeChunkData(BlockColumnProfile[] columnSpanMap)
        {
            // Uniform override short‑circuit path built from batch classification.
            if (_uniformOverride != UniformOverride.None)
            {
                if (_uniformOverride == UniformOverride.AllAir)
                {
                    AllAirChunk = true;
                }
                else if (_uniformOverride == UniformOverride.AllStone)
                {
                    AllStoneChunk = true;
                    CreateUniformSections((ushort)BaseBlockType.Stone);
                }
                else if (_uniformOverride == UniformOverride.AllSoil)
                {
                    AllSoilChunk = true;
                    CreateUniformSections((ushort)BaseBlockType.Soil);
                }
                if (AllStoneChunk || AllSoilChunk)
                {
                    BuildAllBoundaryPlanesInitial();
                }
                return;
            }
            GenerateInitialChunkData(columnSpanMap);
        }

        // NOTE: InitializeChunkData & all generation helpers are in ChunkGenerator partial file.
        public void Render(ShaderProgram shader) => chunkRender?.Render(shader);

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
            // Eager metadata rebuild so downstream flatten has up-to-date representation without on-demand classify cost
            SectionUtils.ClassifyRepresentation(sec);

            // Update per-face full solidity flags when a boundary voxel changes. A face reports solid only if
            // every voxel along that boundary is opaque (TerrainLoader.IsOpaque). Transparent / air break solidity.
            if (lx == 0) FaceSolidNegX = ScanFaceSolidNegX();
            if (lx == dimX - 1) FaceSolidPosX = ScanFaceSolidPosX();
            if (lz == 0) FaceSolidNegZ = ScanFaceSolidNegZ();
            if (lz == dimZ - 1) FaceSolidPosZ = ScanFaceSolidPosZ();
            if (ly == 0) FaceSolidNegY = ScanFaceSolidNegY();
            if (ly == dimY - 1) FaceSolidPosY = ScanFaceSolidPosY();

            // Boundary plane bitsets represent opaque occupancy only. Always update when a boundary cell mutates so
            // transitions opaque<->non-opaque correctly set/clear bits (UpdateBoundaryPlaneBit internally tests IsOpaque).
            if (lx == 0 || lx == dimX - 1 || ly == 0 || ly == dimY - 1 || lz == 0 || lz == dimZ - 1)
            {
                UpdateBoundaryPlaneBit(lx, ly, lz, blockId);
            }
        }

        private bool ScanFaceSolidNegX()
        {
            for (int y = 0; y < dimY; y++)
                for (int z = 0; z < dimZ; z++)
                    if (!TerrainLoader.IsOpaque(GetBlockLocal(0, y, z))) return false;
            return true;
        }
        private bool ScanFaceSolidPosX()
        {
            int x = dimX - 1;
            for (int y = 0; y < dimY; y++)
                for (int z = 0; z < dimZ; z++)
                    if (!TerrainLoader.IsOpaque(GetBlockLocal(x, y, z))) return false;
            return true;
        }
        private bool ScanFaceSolidNegY()
        {
            for (int x = 0; x < dimX; x++)
                for (int z = 0; z < dimZ; z++)
                    if (!TerrainLoader.IsOpaque(GetBlockLocal(x, 0, z))) return false;
            return true;
        }
        private bool ScanFaceSolidPosY()
        {
            int y = dimY - 1;
            for (int x = 0; x < dimX; x++)
                for (int z = 0; z < dimZ; z++)
                    if (!TerrainLoader.IsOpaque(GetBlockLocal(x, y, z))) return false;
            return true;
        }
        private bool ScanFaceSolidNegZ()
        {
            for (int x = 0; x < dimX; x++)
                for (int y = 0; y < dimY; y++)
                    if (!TerrainLoader.IsOpaque(GetBlockLocal(x, y, 0))) return false;
            return true;
        }
        private bool ScanFaceSolidPosZ()
        {
            int z = dimZ - 1;
            for (int x = 0; x < dimX; x++)
                for (int y = 0; y < dimY; y++)
                    if (!TerrainLoader.IsOpaque(GetBlockLocal(x, y, z))) return false;
            return true;
        }
    }
}
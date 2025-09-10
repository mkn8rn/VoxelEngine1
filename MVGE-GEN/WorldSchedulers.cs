using MVGE_GEN.Terrain;
using MVGE_INF.Managers;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace MVGE_GEN
{
    public partial class WorldResources
    {

        private void EnqueueInitialChunkPositions()
        {
            int lodDist = GameManager.settings.lod1RenderDistance;
            int sizeX = GameManager.settings.chunkMaxX;
            int sizeY = GameManager.settings.chunkMaxY;
            int sizeZ = GameManager.settings.chunkMaxZ;
            int verticalRows = lodDist; // current behavior

            int count = 0; // track how many initial chunks we enqueue

            // For each increasing radius ring, enqueue entire vertical stack for each (x,z) column before moving on.
            for (int radius = 0; radius < lodDist; radius++)
            {
                if (radius == 0)
                {
                    // Center column vertical stack
                    for (int vy = 0; vy < verticalRows; vy++)
                    {
                        EnqueueChunkPosition(0, vy * sizeY, 0);
                        count++;
                    }
                    continue;
                }
                int min = -radius;
                int max = radius;
                for (int x = min; x <= max; x++)
                {
                    for (int z = min; z <= max; z++)
                    {
                        // Only perimeter cells (ring)
                        if (Math.Abs(x) != radius && Math.Abs(z) != radius) continue;
                        for (int vy = 0; vy < verticalRows; vy++)
                        {
                            EnqueueChunkPosition(x * sizeX, vy * sizeY, z * sizeZ);
                            count++;
                        }
                    }
                }
            }

            Console.WriteLine($"[World] Scheduled {count} initial LoD1 chunks.");
        }

        private void EnqueueInitialBufferChunkPositions()
        {
            int lodDist = GameManager.settings.lod1RenderDistance;
            int bufferLimit = GameManager.settings.chunkGenerationBufferInitial; // farthest ring for initial buffer pregen
            if (bufferLimit <= lodDist) return; // no buffer range
            int maxRadius = (int)Math.Min(bufferLimit - 1, GameManager.settings.regionWidthInChunks); // exclusive of bufferLimit? we interpret bufferLimit as outside radius count -> schedule up to bufferLimit-1 beyond last lod radius; adjust to inclusive semantics below
            // Clarify: We treat bufferLimit as absolute outer radius inclusive -> schedule radii [lodDist, bufferLimit]
            maxRadius = (int)Math.Min(bufferLimit, GameManager.settings.regionWidthInChunks);
            int sizeX = GameManager.settings.chunkMaxX;
            int sizeY = GameManager.settings.chunkMaxY;
            int sizeZ = GameManager.settings.chunkMaxZ;
            int verticalRows = GameManager.settings.lod1RenderDistance; // reuse vertical range heuristic
            int scheduled = 0;
            // Non-blocking: perform scheduling work on a background task so constructor / caller continues.
            Task.Run(() =>
            {
                for (int radius = lodDist; radius <= maxRadius; radius++)
                {
                    int min = -radius; int max = radius;
                    for (int x = min; x <= max; x++)
                    {
                        for (int z = min; z <= max; z++)
                        {
                            if (Math.Abs(x) != radius && Math.Abs(z) != radius) continue; // perimeter
                            for (int vy = 0; vy < verticalRows; vy++)
                            {
                                int wx = x * sizeX;
                                int wy = vy * sizeY;
                                int wz = z * sizeZ;
                                var key = ChunkIndexKey(wx, wy, wz);
                                if (unbuiltChunks.ContainsKey(key) || activeChunks.ContainsKey(key) || passiveChunks.ContainsKey(key)) continue;
                                if (chunkGenSchedule.ContainsKey(key)) continue;
                                if (bufferGenSchedule.ContainsKey(key)) continue;
                                if (ChunkFileExists(key.cx, key.cy, key.cz)) continue; // already on disk
                                if (!bufferGenSchedule.TryAdd(key, 0)) continue;
                                bufferChunkPositionQueue.Add(new Vector3(wx, wy, wz));
                                scheduled++;
                            }
                        }
                    }
                }
                if (scheduled > 0)
                    Console.WriteLine($"[World] Scheduled {scheduled} initial buffer pregen chunks (radius {lodDist}..{maxRadius}) (async).");
            });
        }

        private void EnqueueRuntimeBufferChunkPositions()
        {
            int lodDist = GameManager.settings.lod1RenderDistance;
            int initialBuffer = GameManager.settings.chunkGenerationBufferInitial;
            int runtimeBuffer = GameManager.settings.chunkGenerationBufferRuntime;
            int startRadius = Math.Max(lodDist, initialBuffer); // schedule beyond what initial pass covered
            if (runtimeBuffer <= startRadius) return; // nothing extra
            int maxRadius = (int)Math.Min(runtimeBuffer, GameManager.settings.regionWidthInChunks);
            int sizeX = GameManager.settings.chunkMaxX;
            int sizeY = GameManager.settings.chunkMaxY;
            int sizeZ = GameManager.settings.chunkMaxZ;
            int verticalRows = GameManager.settings.lod1RenderDistance;
            int scheduled = 0;
            Task.Run(() =>
            {
                for (int radius = startRadius; radius <= maxRadius; radius++)
                {
                    int min = -radius; int max = radius;
                    for (int x = min; x <= max; x++)
                    {
                        for (int z = min; z <= max; z++)
                        {
                            if (Math.Abs(x) != radius && Math.Abs(z) != radius) continue;
                            for (int vy = 0; vy < verticalRows; vy++)
                            {
                                int wx = x * sizeX;
                                int wy = vy * sizeY;
                                int wz = z * sizeZ;
                                var key = ChunkIndexKey(wx, wy, wz);
                                if (unbuiltChunks.ContainsKey(key) || activeChunks.ContainsKey(key) || passiveChunks.ContainsKey(key)) continue;
                                if (chunkGenSchedule.ContainsKey(key)) continue;
                                if (bufferGenSchedule.ContainsKey(key) ) continue;
                                if (ChunkFileExists(key.cx, key.cy, key.cz)) continue; // already on disk
                                if (!bufferGenSchedule.TryAdd(key, 0)) continue;
                                bufferChunkPositionQueue.Add(new Vector3(wx, wy, wz));
                                scheduled++;
                            }
                        }
                    }
                }
                if (scheduled > 0)
                    Console.WriteLine($"[World] Scheduled {scheduled} runtime buffer pregen chunks (radius {startRadius}..{maxRadius}) (async).");
            });
        }

        private void EnqueueUnbuiltChunksForBuild()
        {
            foreach (var key in unbuiltChunks.Keys)
            {
                EnqueueMeshBuild(key, markDirty: false); // initial builds are not dirty rebuilds
            }
        }

        private void EnqueueChunkPosition(int worldX, int worldY, int worldZ, bool force = false)
        {
            var key = ChunkIndexKey(worldX, worldY, worldZ);
            // Region boundary check (inclusive): disallow if any chunk coordinate magnitude exceeds regionWidthInChunks
            long regionLimit = GameManager.settings.regionWidthInChunks; // allowed range: -regionLimit .. +regionLimit
            if (Math.Abs(key.cx) > regionLimit || Math.Abs(key.cy) > regionLimit || Math.Abs(key.cz) > regionLimit)
            {
                return; // outside configured world region
            }
            // If this chunk had previously been scheduled for buffer pre-generation, drop that request now.
            bufferGenSchedule.TryRemove(key, out _);
            // Promotion: if currently passive and becomes force or inside LoD1 scheduling region, move to unbuilt and schedule build.
            if (passiveChunks.TryGetValue(key, out var passive))
            {
                // Determine if inside LoD1 now
                int lodDist = GameManager.settings.lod1RenderDistance;
                if (Math.Abs(key.cx - playerChunkX) <= lodDist && Math.Abs(key.cz - playerChunkZ) <= lodDist && Math.Abs(key.cy - playerChunkY) <= lodDist)
                {
                    if (passiveChunks.TryRemove(key, out var promoted))
                    {
                        unbuiltChunks[key] = promoted;
                        EnqueueMeshBuild(key, markDirty:false);
                        return; // already promoted
                    }
                }
            }
            if (!force)
            {
                if (unbuiltChunks.ContainsKey(key) || activeChunks.ContainsKey(key) || passiveChunks.ContainsKey(key)) return; // already generated or built
                if (!chunkGenSchedule.TryAdd(key, 0)) return; // already queued
            }
            else
            {
                chunkGenSchedule[key] = 0; // overwrite / ensure present
            }
            chunkPositionQueue.Add(new Vector3(worldX, worldY, worldZ));
        }

        private void EnqueueBufferChunkPosition(int worldX, int worldY, int worldZ)
        {
            var key = ChunkIndexKey(worldX, worldY, worldZ);
            long regionLimit = GameManager.settings.regionWidthInChunks;
            if (Math.Abs(key.cx) > regionLimit || Math.Abs(key.cy) > regionLimit || Math.Abs(key.cz) > regionLimit) return;
            // Avoid scheduling if already present or scheduled for active generation
            if (unbuiltChunks.ContainsKey(key) || activeChunks.ContainsKey(key) || passiveChunks.ContainsKey(key)) return;
            if (chunkGenSchedule.ContainsKey(key)) return;
            if (bufferGenSchedule.ContainsKey(key)) return;
            if (ChunkFileExists(key.cx, key.cy, key.cz)) return; // already saved; no need to queue
            if (!bufferGenSchedule.TryAdd(key, 0)) return;
            bufferChunkPositionQueue.Add(new Vector3(worldX, worldY, worldZ));
        }

        private void MarkNeighborsDirty((int cx, int cy, int cz) key, Chunk newChunk)
        {
            foreach (var dir in NeighborDirs)
            {
                var nk = (key.cx + dir.dx, key.cy + dir.dy, key.cz + dir.dz);
                // Only consider already present neighbor chunks (passive neighbors do not need rebuild until promoted)
                bool neighborExists = unbuiltChunks.ContainsKey(nk) || activeChunks.ContainsKey(nk);
                if (!neighborExists) continue;

                if (!HasAnySolidOnBoundary(newChunk, dir)) continue;

                // Mark dirty & enqueue (coalesce multiple marks by using dirtyChunks)
                if (dirtyChunks.TryAdd(nk, 0))
                {
                    EnqueueMeshBuild(nk, markDirty: true);
                }
            }
        }

        private void EnqueueMeshBuild((int cx, int cy, int cz) key, bool markDirty = true)
        {
            // If marking dirty, ensure chunk is marked dirty even if not enqueued for build (will be built next time)
            if (markDirty)
            {
                // Ensure dirty flag exists (idempotent)
                dirtyChunks.TryAdd(key, 0);
            }

            // Only enqueue if chunk is present (unbuilt or active)
            if (!unbuiltChunks.ContainsKey(key) && !activeChunks.ContainsKey(key)) return;

            // Skip if already built and not dirty
            if (activeChunks.ContainsKey(key) && !dirtyChunks.ContainsKey(key)) return;

            if (meshBuildSchedule.TryAdd(key, 0))
            {
                meshBuildQueue.Add(key);
            }
        }

        private void PruneOutOfRangeBufferChunks(int playerCx, int playerCy, int playerCz)
        {
            int lodDist = GameManager.settings.lod1RenderDistance; // vertical heuristic
            int verticalRange = lodDist; // keep consistent with scheduling
            int bufferRadius = currentBufferRadius;
            if (bufferRadius <= 0) return;
            foreach (var key in bufferGenSchedule.Keys.ToArray())
            {
                // If chunk already promoted to active scheduling, skip (it will be removed elsewhere)
                if (chunkGenSchedule.ContainsKey(key) || unbuiltChunks.ContainsKey(key) || activeChunks.ContainsKey(key) || passiveChunks.ContainsKey(key))
                {
                    // Ensure it is not still marked as buffer
                    bufferGenSchedule.TryRemove(key, out _);
                    continue;
                }
                // Cull when outside current buffer horizon or vertical range
                if (Math.Abs(key.cx - playerCx) > bufferRadius || Math.Abs(key.cz - playerCz) > bufferRadius || Math.Abs(key.cy - playerCy) > verticalRange)
                {
                    bufferGenSchedule.TryRemove(key, out _); // generation worker will skip dequeued stale entries
                }
            }
        }
        private void ScheduleChunksAroundPlayer(int centerCx, int centerCy, int centerCz)
        {
            // Proactively ensure batches in active +1 ring are present (load from disk if exist)
            EnsureBatchesForActiveArea(centerCx, centerCz);

            int lodDist = GameManager.settings.lod1RenderDistance;
            int bufferRadius = currentBufferRadius; // dynamic buffer radius
            int sizeX = GameManager.settings.chunkMaxX;
            int sizeY = GameManager.settings.chunkMaxY;
            int sizeZ = GameManager.settings.chunkMaxZ;
            int verticalRows = lodDist; // number of vertical layers to consider
            long regionLimit = GameManager.settings.regionWidthInChunks; // vertical clamp as well

            // Compute symmetric vertical window around player chunk Y
            if (verticalRows < 1) verticalRows = 1;
            // we expand vertical range to full offset in both directions.
            int maxVerticalOffset = lodDist;
            int vMinLayer = centerCy - maxVerticalOffset;
            int vMaxLayer = centerCy + maxVerticalOffset;
            // Clamp to region vertical limits
            if (vMinLayer < -regionLimit) vMinLayer = (int)-regionLimit;
            if (vMaxLayer > regionLimit) vMaxLayer = (int)regionLimit;

            // Active generation rings (LoD1)
            for (int radius = 0; radius <= lodDist; radius++)
            {
                if (radius == 0)
                {
                    for (int cy = vMinLayer; cy <= vMaxLayer; cy++)
                    {
                        EnqueueChunkPosition(centerCx * sizeX, cy * sizeY, centerCz * sizeZ);
                    }
                    continue;
                }
                int min = -radius; int max = radius;
                for (int dx = min; dx <= max; dx++)
                {
                    for (int dz = min; dz <= max; dz++)
                    {
                        if (Math.Abs(dx) != radius && Math.Abs(dz) != radius) continue; // ring perimeter
                        for (int cy = vMinLayer; cy <= vMaxLayer; cy++)
                        {
                            int wx = (centerCx + dx) * sizeX;
                            int wy = cy * sizeY;
                            int wz = (centerCz + dz) * sizeZ;
                            EnqueueChunkPosition(wx, wy, wz);
                        }
                    }
                }
            }

            // Buffer (initial or runtime depending on currentBufferRadius) beyond LoD1
            if (bufferRadius > lodDist)
            {
                int maxRuntimeRadius = (int)Math.Min(bufferRadius, GameManager.settings.regionWidthInChunks);
                for (int radius = lodDist + 1; radius <= maxRuntimeRadius; radius++) // start just beyond inclusive active radius
                {
                    int min = -radius; int max = radius;
                    for (int dx = min; dx <= max; dx++)
                    {
                        for (int dz = min; dz <= max; dz++)
                        {
                            if (Math.Abs(dx) != radius && Math.Abs(dz) != radius) continue;
                            for (int cy = vMinLayer; cy <= vMaxLayer; cy++)
                            {
                                int wx = (centerCx + dx) * sizeX;
                                int wy = cy * sizeY;
                                int wz = (centerCz + dz) * sizeZ;
                                EnqueueBufferChunkPosition(wx, wy, wz);
                            }
                        }
                    }
                }
            }
        }

        // Unload chunks (active or unbuilt) outside of current interest radius.
        private void UnloadFarChunks(int centerCx, int centerCy, int centerCz)
        {
            int lodDist = GameManager.settings.lod1RenderDistance;
            int verticalRange = lodDist;

            // Track which batches might become empty
            var candidateBatches = new HashSet<(int bx,int bz)>();

            void TrackBatch((int cx,int cy,int cz) key)
            {
                var (bx,bz) = Quadrant.GetBatchIndices(key.cx, key.cz);
                candidateBatches.Add((bx,bz));
            }

            foreach (var key in activeChunks.Keys.ToArray())
            {
                if (Math.Abs(key.cx - centerCx) > lodDist || Math.Abs(key.cz - centerCz) > lodDist || Math.Abs(key.cy - centerCy) > verticalRange)
                {
                    if (activeChunks.TryRemove(key, out var chunk))
                    {
                        chunk.chunkRender?.ScheduleDelete();
                        dirtyChunks.TryRemove(key, out _);
                        meshBuildSchedule.TryRemove(key, out _);
                        TrackBatch(key);
                    }
                }
            }
            // Unbuilt chunks (cancel generation / building if far)
            foreach (var key in unbuiltChunks.Keys.ToArray())
            {
                if (Math.Abs(key.cx - centerCx) > lodDist || Math.Abs(key.cz - centerCz) > lodDist || Math.Abs(key.cy - centerCy) > verticalRange)
                {
                    if (unbuiltChunks.TryRemove(key, out _))
                    {
                        cancelledChunks[key] = 0;
                        chunkGenSchedule.TryRemove(key, out _);
                        meshBuildSchedule.TryRemove(key, out _);
                        dirtyChunks.TryRemove(key, out _);
                        TrackBatch(key);
                    }
                }
            }
            // Passive chunks beyond +1 ring can be culled as well (outside lodDist+1)
            foreach (var key in passiveChunks.Keys.ToArray())
            {
                if (Math.Abs(key.cx - centerCx) > lodDist + 1 || Math.Abs(key.cz - centerCz) > lodDist + 1 || Math.Abs(key.cy - centerCy) > verticalRange)
                {
                    if (passiveChunks.TryRemove(key, out _)) TrackBatch(key);
                }
                // Promotion if moved into LoD1 vertical + horizontal bounds
                else if (Math.Abs(key.cx - centerCx) <= lodDist && Math.Abs(key.cz - centerCz) <= lodDist && Math.Abs(key.cy - centerCy) <= verticalRange)
                {
                    if (passiveChunks.TryRemove(key, out var promoted))
                    {
                        unbuiltChunks[key] = promoted;
                        EnqueueMeshBuild(key, markDirty:false);
                    }
                }
            }

            // After removals, attempt to save & remove empty batches
            foreach (var (bx,bz) in candidateBatches)
            {
                TrySaveAndRemoveBatch(bx,bz);
            }
            // Buffer-generated chunks are never retained in-memory, so no unload needed for those.
        }

        // Extend scheduling: ensure LoD1 + 1 ring batches are loaded in memory.
        private void EnsureBatchesForActiveArea(int centerCx,int centerCz)
        {
            int lodDist = GameManager.settings.lod1RenderDistance + 1; // +1 ring per new design
            for (int dx = -lodDist; dx <= lodDist; dx++)
            {
                for (int dz = -lodDist; dz <= lodDist; dz++)
                {
                    int cx = centerCx + dx;
                    int cz = centerCz + dz;
                    var (bx,bz) = Quadrant.GetBatchIndices(cx, cz);
                    // Touch batch (forces placeholder creation or load if file exists)
                    if (!loadedBatches.ContainsKey((bx,bz)))
                    {
                        if (BatchFileExists(bx,bz))
                        {
                            LoadBatchForChunk(cx, centerCz, cz); // vertical index not needed for batch load
                        }
                        else
                        {
                            // If no batch file, will be populated lazily as chunks generate.
                            GetOrCreateBatch(bx,bz);
                        }
                    }
                }
            }
        }
    }
}

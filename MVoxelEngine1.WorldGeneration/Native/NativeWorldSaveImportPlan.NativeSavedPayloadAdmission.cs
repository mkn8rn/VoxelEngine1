using Supprocom.NativeAllocationManagement;

namespace MVoxelEngine1.WorldGeneration.Native;
internal sealed partial class NativeWorldSaveImportPlan
{
    private readonly NativeLeaseAction<byte> reserveSavedChunksAction;
    private readonly NativeLeaseAction<byte> selectSavedChunksAction;
    private readonly NativeLeaseAction<byte> selectBlockSourceAction;
    private readonly NativeLeaseAction<byte> captureSourceAction;
    private readonly NativeLeaseAction<byte> rollbackSourceImportsAction;
    private readonly int[] savedCandidates;
    private readonly FileStream? [] savedStreams;
    private int savedCandidateCount;
    private int remainingDeferredCount;
    private int pendingCenterX;
    private int pendingCenterY;
    private int pendingCenterZ;
    private int pendingSourceIndex;
    private NativeMaterializedChunkRecord pendingSource;
    private NativeMaterializedStorageRequirements admissionBefore;
    private NativeMaterializedStorageRequirements admissionRequired;
    internal bool HasDeferredChunks => remainingDeferredCount != 0;
    internal Action<int>? BeforeDeferredImportForTesting { get; set; }

    internal void VerifyDeferredSourceFiles()
    {
        if (!HasDeferredChunks)
            return;
        foreach (SavedFile file in files)
        {
            using FileStream stream = OpenRead(file.Path);
            VerifySavedFile(stream, file);
        }
    }

    internal void PrepareLazyImport(NativeGtrtSession session)
    {
        if (!HasSavedChunks)
            return;
        foreach (SavedFile file in files)
        {
            FileStream? stream = null;
            try
            {
#pragma warning disable CA2000 // This stream has unconditional finally disposal; ValidateFile uses a leave-open BinaryReader and does not own the stream.
                stream = OpenRead(file.Path);
#pragma warning restore CA2000
                VerifySavedFile(stream, file);
                ValidateFile(stream, file, session);
            }
            finally
            {
                stream?.Dispose();
            }
        }

        ReserveSavedMetadata(session);
        EnsureResidentPayloads(session, 0, 0, 0);
    }

    internal void ReserveSavedMetadata(NativeGtrtSession session) => session.Access(reserveSavedChunksAction);
    private void ReserveSavedChunksCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        remainingDeferredCount = 0;
        foreach (SavedChunkDescriptor descriptor in savedChunks)
        {
            if (!NativeMaterializedTerrain.TryReserveSavedChunk(ref view, descriptor.X, descriptor.Y, descriptor.Z, out int index))
                throw new InvalidDataException("Saved chunk metadata cannot enter the native coordinate index.");
            ref NativeMaterializedChunkRecord chunk = ref view.MaterializedChunks[index];
            if (chunk.State != NativeMaterializedTerrain.DeferredRecord)
                continue;
            chunk.SavedSource = descriptor.Source;
            chunk.Temperature = descriptor.Temperature;
            chunk.Humidity = descriptor.Humidity;
            remainingDeferredCount++;
        }
    }

    internal void EnsureResidentPayloads(NativeGtrtSession session, int centerX, int centerY, int centerZ)
    {
        if (!HasDeferredChunks)
            return;
        pendingCenterX = centerX;
        pendingCenterY = centerY;
        pendingCenterZ = centerZ;
        session.Access(selectSavedChunksAction);
        LoadSelectedPayloads(session);
    }

    internal void EnsureBlockPayload(NativeGtrtSession session, int chunkX, int chunkY, int chunkZ, bool inspectionActive)
    {
        if (!HasDeferredChunks)
            return;
        pendingCenterX = chunkX;
        pendingCenterY = chunkY;
        pendingCenterZ = chunkZ;
        session.Access(selectBlockSourceAction);
        if (savedCandidateCount != 0 && inspectionActive)
            throw new InvalidOperationException("A deferred saved chunk cannot load during native packet inspection.");
        LoadSelectedPayloads(session);
    }

    private static NativeMaterializedStorageRequirements UsedStorage(scoped ref NativeGtrtSessionView view) => new(view.State.MaterializedChunkCount, view.State.MaterializedSectionCount, view.State.MaterializedRawSectionCount, view.State.MaterializedPaletteCursor, view.State.MaterializedPackedWordCursor);
    private void SelectSavedChunksCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        savedCandidateCount = 0;
        admissionBefore = UsedStorage(ref view);
        admissionRequired = admissionBefore;
        int radius = view.SessionHeader.ResidentRadius;
        for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
                for (int y = -radius; y <= radius; y++)
                    SelectCandidate(ref view, checked(pendingCenterX + x), checked(pendingCenterY + y), checked(pendingCenterZ + z));
    }

    private void SelectBlockSourceCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        savedCandidateCount = 0;
        admissionBefore = UsedStorage(ref view);
        admissionRequired = admissionBefore;
        SelectCandidate(ref view, pendingCenterX, pendingCenterY, pendingCenterZ);
    }

    private void SelectCandidate(scoped ref NativeGtrtSessionView view, int x, int y, int z)
    {
        int index = view.FindMaterializedChunkIndex(x, y, z);
        if (index < 0 || view.MaterializedChunks[index].State != NativeMaterializedTerrain.DeferredRecord)
            return;
        savedCandidates[savedCandidateCount++] = index;
        NativeSavedChunkSource source = view.MaterializedChunks[index].SavedSource;
        admissionRequired = new NativeMaterializedStorageRequirements(admissionRequired.ChunkCount, checked(admissionRequired.SectionCount + source.SectionCount), checked(admissionRequired.RawSectionCount + source.RawSectionCount), checked(admissionRequired.PaletteCount + source.PaletteCount), checked(admissionRequired.PackedWordCount + source.PackedWordCount));
    }

    private void CaptureSourceCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        pendingSource = view.MaterializedChunks[pendingSourceIndex];
    }

    private void LoadSelectedPayloads(NativeGtrtSession session)
    {
        if (savedCandidateCount == 0)
            return;
        bool importStarted = false;
        try
        {
            // Keep each verified file open until all selected payloads are validated and imported.
            for (int candidate = 0; candidate < savedCandidateCount; candidate++)
            {
                ReadSelectedPayload(session, candidate);
                pendingValidationSucceeded = false;
                session.Access(validateChunkAction);
                if (!pendingValidationSucceeded)
                    throw new InvalidDataException("A deferred saved chunk contains an invalid runtime block.");
            }

            session.EnsureMaterializedCapacity(admissionRequired);
            importStarted = true;
            for (int candidate = 0; candidate < savedCandidateCount; candidate++)
            {
                ReadSelectedPayload(session, candidate);
                BeforeDeferredImportForTesting?.Invoke(candidate);
                pendingImportSucceeded = false;
                session.Access(importChunkAction);
                if (!pendingImportSucceeded)
                    throw new InvalidDataException("A deferred saved chunk could not enter native storage.");
            }

            remainingDeferredCount -= savedCandidateCount;
        }
        catch
        {
            if (importStarted)
                session.Access(rollbackSourceImportsAction);
            throw;
        }
        finally
        {
            pendingPayload = null;
            foreach (ref FileStream? stream in savedStreams.AsSpan())
            {
                stream?.Dispose();
                stream = null;
            }
        }
    }

    private void ReadSelectedPayload(NativeGtrtSession session, int candidate)
    {
        pendingSourceIndex = savedCandidates[candidate];
        session.Access(captureSourceAction);
        NativeSavedChunkSource source = pendingSource.SavedSource;
        FileStream? stream = savedStreams[source.FileIndex];
        if (stream is null)
        {
            stream = OpenRead(files[source.FileIndex].Path);
            savedStreams[source.FileIndex] = stream;
            VerifySavedFile(stream, files[source.FileIndex]);
        }

        stream.Position = source.PayloadOffset;
        pendingPayload = new byte[source.PayloadByteCount];
        stream.ReadExactly(pendingPayload);
        pendingChunkX = pendingSource.ChunkX;
        pendingChunkY = pendingSource.ChunkY;
        pendingChunkZ = pendingSource.ChunkZ;
    }

    private static void VerifySavedFile(FileStream stream, SavedFile file)
    {
        stream.Position = 0;
        byte[] hash = System.Security.Cryptography.SHA256.HashData(stream);
        if (!hash.AsSpan().SequenceEqual(file.Sha256))
            throw new InvalidDataException($"The saved quad changed during native import: {file.Path}");
        stream.Position = 0;
    }

    private void RollbackSourceImportsCore(scoped NativeLeaseView<byte> owner)
    {
        var view = new NativeGtrtSessionView(owner.AsSpan());
        foreach (ref readonly int index in savedCandidates.AsSpan(0, savedCandidateCount))
        {
            ref NativeMaterializedChunkRecord chunk = ref view.MaterializedChunks[index];
            chunk.State = NativeMaterializedTerrain.DeferredRecord;
            chunk.StorageKind = NativeChunkStorageKind.DeferredSaved;
            chunk.Revision = 0;
            chunk.PersistedRevision = 0;
            chunk.UniformBlockId = 0;
            view.MaterializedSectionMaps.Slice(chunk.SectionMapOffset, view.SectionsPerChunk).Fill(-1);
            int active = view.GetChunkIndex(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ);
            if (active >= 0)
            {
                view.Chunks[active].MaterializedChunkIndex = -1;
                view.Chunks[active].StorageKind = NativeChunkStorageKind.GeneratedProfile;
                view.Chunks[active].DirtyRevision = 0;
            }
        }

        view.State.MaterializedSectionCount = admissionBefore.SectionCount;
        view.State.MaterializedRawSectionCount = admissionBefore.RawSectionCount;
        view.State.MaterializedPaletteCursor = admissionBefore.PaletteCount;
        view.State.MaterializedPackedWordCursor = admissionBefore.PackedWordCount;
        view.State.FailureCode = 0;
    }
}

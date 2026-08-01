using MVoxelEngine1.WorldGeneration;

namespace MVoxelEngine1.Tests
{
    public class CanonicalRenderFaceHasherTests
    {
        private static readonly CanonicalRenderFace OpaqueFace = new(
            -1,
            7,
            9,
            0,
            CanonicalRenderPass.Opaque,
            2,
            0);

        private static readonly CanonicalRenderFace TransparentFace = new(
            16,
            -2,
            33,
            5,
            CanonicalRenderPass.Transparent,
            11,
            0);

        [Fact]
        public void HashIsIndependentOfEmissionOrder()
        {
            CanonicalFaceSetDigest forward = CanonicalRenderFaceHasher.Hash(
                new[] { OpaqueFace, TransparentFace });
            CanonicalFaceSetDigest reverse = CanonicalRenderFaceHasher.Hash(
                new[] { TransparentFace, OpaqueFace });

            Assert.Equal(forward.Sha256, reverse.Sha256);
            Assert.Equal(
                "80830FBB59916DFAF4BEBF26544212C97C75F7CF606E31E09AA1A6B8B2229C35",
                forward.Sha256);
            Assert.Equal(2, forward.FaceCount);
            Assert.Equal(1, forward.OpaqueFaceCount);
            Assert.Equal(1, forward.TransparentFaceCount);
        }

        [Fact]
        public void EachIdentityFieldChangesTheHash()
        {
            string baseline = CanonicalRenderFaceHasher.Hash(
                new[] { TransparentFace }).Sha256;
            CanonicalRenderFace[] mutations =
            {
                TransparentFace with { WorldX = 17 },
                TransparentFace with { WorldY = -1 },
                TransparentFace with { WorldZ = 34 },
                TransparentFace with { Direction = 4 },
                TransparentFace with { RenderPass = CanonicalRenderPass.Opaque },
                TransparentFace with { BlockId = 12 },
                TransparentFace with { NeighborBlockId = 11 }
            };

            Assert.All(
                mutations,
                mutation => Assert.NotEqual(
                    baseline,
                    CanonicalRenderFaceHasher.Hash(new[] { mutation }).Sha256));
        }

        [Fact]
        public void DuplicateFaceIsRejected()
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => CanonicalRenderFaceHasher.Hash(
                    new[] { TransparentFace, TransparentFace }));

            Assert.Contains("Duplicate canonical face", exception.Message);
        }
    }
}

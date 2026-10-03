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

        [Fact]
        public void BufferedHashesMatchIndependentLittleEndianKnownVectors()
        {
            var faces = new CanonicalRenderFace[4096];
            for (int i = 0; i < faces.Length; i++)
                faces[i] = new CanonicalRenderFace(i - 2048, i % 17 - 8, i * 13 % 29,
                    (byte)(i % 6), i % 5 == 0 ? CanonicalRenderPass.Opaque : CanonicalRenderPass.Transparent,
                    (ushort)(i % 403 + 1), (ushort)(i % 7));

            CanonicalFaceSetDigest digest = CanonicalRenderFaceHasher.HashOrderedBatches(
                new[] { faces.Take(7), faces.Skip(7).Take(1010), faces.Skip(1017) });
            Assert.Equal("7C81DCE6EB1EBE9E9D8CCDD0EB49B83DD9DD23A816211DBC9A97767A658E7605", digest.Sha256);
            Assert.Equal("B41F9C314E685CE096DB28D33CCB15DDBA1B13BD5603D5E237C6554FC183A773", digest.OpaqueSha256);
            Assert.Equal("9B035749D196A5774DC8FC799673A66D86392C7B791C378697BBAA1ECFBBAEB7", digest.TransparentSha256);
            string[] opaqueDirections =
            [
                "AE34EDBE5A88C2F7EB4FAD58C02F9AE2F4BCF5CAC27F8524AB62B39E90471ED2",
                "60473B623A029B34B5E10DC2C25C8D55AAE364DFE719C396E8AC383EDB5BF39D",
                "0241CF162B1CFC3B641575EE223F024C739E77924E4885FF11F24E0AAA14C148",
                "223F8AE65261C6DA4E30D20AD38093743CBB9E7561F12E8E070DF714696799B2",
                "29B491D94DE62F52A24ED7583297C00382F1DC694DC4A6388DD01B349C5D2F2F",
                "AE350B624413E3EF1F3162E9125728AC4122849CC005ABA3007EB57EB33D37F9"
            ];
            string[] transparentDirections =
            [
                "53378912B9BA939C3906BDC885FA5482B40F972309E627868B46473173A3A3ED",
                "BA97498506DDE55B7CE8CD0AADD863FFDBBFF536482C17998F29FF2F8189DA58",
                "EE8A6F02905728EDC528F597DABFCE00497527A22F0FFEB543F58CEA9D048CA6",
                "5C1C359C0A00B30FA8F6C39943DD52F4C9CAAEF021E583CCF9CBA658B19B3083",
                "223AC9D384975B1A24462DE473648AFDA06BD9CC7EECC205E45792B808497BF3",
                "3442DAA2B541F72C2B3DDA88F4ACE33A63B108F9CC6A7665BADFC4EE80669C0D"
            ];
            Assert.Equal(opaqueDirections, digest.OpaqueDirections.Select(static direction => direction.Sha256));
            Assert.Equal(transparentDirections, digest.TransparentDirections.Select(static direction => direction.Sha256));
        }

        [Fact]
        public void OrderedBatchesMatchCompleteHash()
        {
            CanonicalRenderFace first = OpaqueFace with { WorldX = -20 };
            CanonicalRenderFace second = OpaqueFace with { WorldX = -3 };
            CanonicalRenderFace third = TransparentFace with { WorldX = 16 };
            CanonicalRenderFace fourth = TransparentFace with { WorldX = 40 };
            CanonicalRenderFace[] all = { fourth, second, first, third };

            CanonicalFaceSetDigest complete = CanonicalRenderFaceHasher.Hash(all);
            CanonicalFaceSetDigest batches =
                CanonicalRenderFaceHasher.HashOrderedBatches(
                    new[]
                    {
                        new[] { second, first },
                        new[] { fourth, third }
                    });

            Assert.Equal(complete.Sha256, batches.Sha256);
            Assert.Equal(complete.OpaqueSha256, batches.OpaqueSha256);
            Assert.Equal(complete.TransparentSha256, batches.TransparentSha256);
            Assert.Equal(complete.FaceCount, batches.FaceCount);
        }
    }
}

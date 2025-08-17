using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_GFX.Models
{
    public struct RawFaceData
    {
        public static readonly Dictionary<Faces, List<ByteVector3>> rawVertexData = new Dictionary<Faces, List<ByteVector3>>
        {
            {
                Faces.FRONT, new List<ByteVector3>()
                {
                    new ByteVector3{ x = 0, y = 1, z = 1 }, // topleft vert
                    new ByteVector3{ x = 1, y = 1, z = 1 }, // topright vert
                    new ByteVector3{ x = 1, y = 0, z = 1 }, // topleft vert
                    new ByteVector3{ x = 0, y = 0, z = 1 }, // topleft vert
                }
            },

            {
                Faces.BACK, new List<ByteVector3>()
                {
                    new ByteVector3{ x = 1, y = 1, z = 0 }, // topleft vert
                    new ByteVector3{ x = 0, y = 1, z = 0 }, // topright vert
                    new ByteVector3{ x = 0, y = 0, z = 0 }, // topleft vert
                    new ByteVector3{ x = 1, y = 0, z = 0 }, // topleft vert
                }
            },

            {
                Faces.LEFT, new List<ByteVector3>()
                {
                    new ByteVector3{ x = 0, y = 1, z = 0 }, // topleft vert
                    new ByteVector3{ x = 0, y = 1, z = 1 }, // topright vert
                    new ByteVector3{ x = 0, y = 0, z = 1 }, // topleft vert
                    new ByteVector3{ x = 0, y = 0, z = 0 }, // topleft vert
                }
            },

            {
                Faces.RIGHT, new List<ByteVector3>()
                {
                    new ByteVector3{ x = 1, y = 1, z = 1 }, // topleft vert
                    new ByteVector3{ x = 1, y = 1, z = 0 }, // topright vert
                    new ByteVector3{ x = 1, y = 0, z = 0 }, // topleft vert
                    new ByteVector3{ x = 1, y = 0, z = 1 }, // topleft vert
                }
            },

            {
                Faces.TOP, new List<ByteVector3>()
                {
                    new ByteVector3{ x = 0, y = 1, z = 0 }, // topleft vert
                    new ByteVector3{ x = 1, y = 1, z = 0 }, // topright vert
                    new ByteVector3{ x = 1, y = 1, z = 1 }, // topleft vert
                    new ByteVector3{ x = 0, y = 1, z = 1 }, // topleft vert
                }
            },

            {
                Faces.BOTTOM, new List<ByteVector3>()
                {
                    new ByteVector3{ x = 0, y = 0, z = 1 }, // topleft vert
                    new ByteVector3{ x = 1, y = 0, z = 1 }, // topright vert
                    new ByteVector3{ x = 1, y = 0, z = 0 }, // topleft vert
                    new ByteVector3{ x = 0, y = 0, z = 0 }, // topleft vert
                }
            },

        };
    }
}

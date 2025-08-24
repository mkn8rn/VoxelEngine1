using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_INF.Models.Terrain
{
    public class BlockType
    {
        public required ushort ID { get; set; }
        public required string UniqueName { get; set; }
        public required string Name { get; set; }
        public required BaseBlockType BaseType { get; set; }
        public required string TextureFaceBase { get; set; }
        public required string TextureFaceTop { get; set; }
        public required string TextureFaceFront { get; set; }
        public required string TextureFaceBack { get; set; }
        public required string TextureFaceLeft { get; set; }
        public required string TextureFaceRight { get; set; }
        public required string TextureFaceBottom { get; set; }

    }
}

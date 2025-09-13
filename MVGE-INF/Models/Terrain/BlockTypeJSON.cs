using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MVGE_INF.Models.Terrain
{
    public struct BlockTypeJSON
    {
        public ushort? ID { get; set; }
        public required string Name { get; set; }
        public required BaseBlockType BaseType { get; set; }
        public required string TextureFaceBase { get; set; }
        public string? TextureFaceTop { get; set; }
        public string? TextureFaceFront { get; set; }
        public string? TextureFaceBack { get; set; }
        public string? TextureFaceLeft { get; set; }
        public string? TextureFaceRight { get; set; }
        public string? TextureFaceBottom { get; set; }
        public required bool IsTransparent { get; set; }
        public required BlockStateOfMatter StateOfMatter { get; set; }
    }
}

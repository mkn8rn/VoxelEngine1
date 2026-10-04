namespace MVoxelEngine1.Infrastructure.Models.Generation
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public record struct BlockColumnProfile
    {
        public int StoneStart { readonly get; set; }
        public int StoneEnd { readonly get; set; }
        public int SoilStart { readonly get; set; }
        public int SoilEnd { readonly get; set; }
        public int WaterStart { readonly get; set; }
        public int WaterEnd { readonly get; set; }
    }

}

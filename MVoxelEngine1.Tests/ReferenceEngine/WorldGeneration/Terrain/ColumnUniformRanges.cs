namespace MVoxelEngine1.WorldGeneration.Terrain
{
    internal struct ColumnUniformRanges
    {
        public bool HasMaterial;
        public int MinimumMaterialStart;
        public int MaximumMaterialEnd;
        public bool AllColumnsHaveStone;
        public int StoneStartMinimum;
        public int StoneStartMaximum;
        public int StoneEndMinimum;
        public int StoneEndMaximum;
        public bool AllColumnsHaveSoil;
        public int SoilStartMinimum;
        public int SoilStartMaximum;
        public int SoilEndMinimum;
        public int SoilEndMaximum;
        public bool AllColumnsHaveWater;
        public int WaterStartMinimum;
        public int WaterStartMaximum;
        public int WaterEndMinimum;
        public int WaterEndMaximum;
    }

}

struct DrawElementsIndirectCommand {
    uint Count;
    uint InstanceCount;
    uint FirstIndex;
    int FirstVertex;
    uint BaseInstance;
    
    int ChunkCenterX;
    int ChunkCenterY;
    int ChunkCenterZ;
    int LodLevel;
};
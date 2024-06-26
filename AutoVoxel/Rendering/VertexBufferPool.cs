﻿using AutoVoxel.Data;
using Horizon.Core.Data;
using Horizon.Engine;
using Horizon.OpenGL.Buffers;

namespace AutoVoxel.Rendering;

public class VertexBufferPool
{
    private int maximumCount = 4096;
    private Queue<VertexBufferObject> freeBuffers;
    private List<VertexBufferObject> usedBuffers;
    private int activeCount { get => freeBuffers.Count + usedBuffers.Count; }

    public VertexBufferPool()
    {
        freeBuffers = new();
        usedBuffers = new();
    }

    public void Initialize()
    {
        for (int i = 0; i < maximumCount / 8; i++)
        {
            freeBuffers.Enqueue(CreateVertexBuffer());
        }
    }

    public VertexBufferObject Get()
    {
        if (freeBuffers.Any())
        {
            var buf = freeBuffers.Dequeue();
            usedBuffers.Add(buf);
            return buf;
        }

        if (activeCount + 1 > maximumCount)
            throw new Exception("Out of space!");

        var buffer = CreateVertexBuffer();

        usedBuffers.Add(buffer);
        return buffer;
    }

    public void Return(in VertexBufferObject vbo)
    {
        usedBuffers.Remove(vbo);
        freeBuffers.Enqueue(vbo);
    }

    private static VertexBufferObject CreateVertexBuffer()
    {
        var buffer = new VertexBufferObject(GameEngine.Instance.ObjectManager.VertexArrays.Create(Horizon.OpenGL.Descriptions.VertexArrayObjectDescription.VertexBuffer));

        buffer.Bind();
        buffer.VertexBuffer.Bind();
        buffer.VertexBuffer.SetLayout<ChunkVertex>();
        buffer.VertexBuffer.Unbind();
        buffer.Unbind();

        return buffer;
    }
}

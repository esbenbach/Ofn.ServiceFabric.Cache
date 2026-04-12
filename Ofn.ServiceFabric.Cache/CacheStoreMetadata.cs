using Microsoft.ServiceFabric.Data;
using System.IO;

namespace Ofn.ServiceFabric.Cache;

public sealed class CacheStoreMetadata
{
    public CacheStoreMetadata(long size, string? firstCacheKey, string? lastCacheKey)
    {
        Size = size;
        FirstCacheKey = firstCacheKey;
        LastCacheKey = lastCacheKey;
    }

    public long Size { get; private set; }
    public string? FirstCacheKey { get; private set; }
    public string? LastCacheKey { get; private set; }
}

class CacheStoreMetadataSerializer : IStateSerializer<CacheStoreMetadata>
{
    // NOTE: Size changed from int (4 bytes) to long (8 bytes) in v2 format.
    // Existing replicas must be drained before upgrading.
    CacheStoreMetadata IStateSerializer<CacheStoreMetadata>.Read(BinaryReader reader)
    {
        return new CacheStoreMetadata(
            reader.ReadInt64(),
            GetStringValueOrNull(reader.ReadString()),
            GetStringValueOrNull(reader.ReadString())
            );
    }

    void IStateSerializer<CacheStoreMetadata>.Write(CacheStoreMetadata value, BinaryWriter writer)
    {
        writer.Write(value.Size);
        writer.Write(value.FirstCacheKey ?? string.Empty);
        writer.Write(value.LastCacheKey ?? string.Empty);
    }

    // Read overload for differential de-serialization
    CacheStoreMetadata IStateSerializer<CacheStoreMetadata>.Read(CacheStoreMetadata baseValue, BinaryReader reader)
    {
        return ((IStateSerializer<CacheStoreMetadata>)this).Read(reader);
    }

    // Write overload for differential serialization
    void IStateSerializer<CacheStoreMetadata>.Write(CacheStoreMetadata baseValue, CacheStoreMetadata newValue, BinaryWriter writer)
    {
        ((IStateSerializer<CacheStoreMetadata>)this).Write(newValue, writer);
    }

    private string GetStringValueOrNull(string value)
    {
        return value == string.Empty ? null : value;
    }
}

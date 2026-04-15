using Microsoft.ServiceFabric.Data;
using System.IO;

namespace Ofn.ServiceFabric.Cache;

/// <summary>
/// Metadata record tracking the aggregate size and head/tail keys of the LRU linked list for a single partition.
/// </summary>
public sealed class CacheStoreMetadata
{
    /// <summary>
    /// Initializes a new <see cref="CacheStoreMetadata"/>.
    /// </summary>
    /// <param name="size">Current aggregate byte size of all cached items in this partition.</param>
    /// <param name="firstCacheKey">Key of the least-recently-used (LRU) item, or <c>null</c> when empty.</param>
    /// <param name="lastCacheKey">Key of the most-recently-used (MRU) item, or <c>null</c> when empty.</param>
    public CacheStoreMetadata(long size, string? firstCacheKey, string? lastCacheKey)
    {
        Size = size;
        FirstCacheKey = firstCacheKey;
        LastCacheKey = lastCacheKey;
    }

    /// <summary>Current aggregate byte size of all cached items in this partition.</summary>
    public long Size { get; private set; }

    /// <summary>Key of the least-recently-used (LRU) item, or <c>null</c> when empty.</summary>
    public string? FirstCacheKey { get; private set; }

    /// <summary>Key of the most-recently-used (MRU) item, or <c>null</c> when empty.</summary>
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

    private string? GetStringValueOrNull(string value)
    {
        return value == string.Empty ? null : value;
    }
}

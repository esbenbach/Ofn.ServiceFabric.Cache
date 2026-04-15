namespace Ofn.ServiceFabric.Cache;

using Microsoft.ServiceFabric.Data;
using System;
using System.IO;

/// <summary>
/// Value object stored in the cache Reliable Dictionary, carrying the payload and LRU linked-list pointers.
/// </summary>
public sealed class CachedItem
{
    /// <summary>
    /// Initializes a new <see cref="CachedItem"/>.
    /// </summary>
    /// <param name="value">Raw cached bytes.</param>
    /// <param name="beforeCacheKey">Key of the preceding item in the LRU linked list, or <c>null</c> if this is the first item.</param>
    /// <param name="afterCacheKey">Key of the following item in the LRU linked list, or <c>null</c> if this is the last item.</param>
    /// <param name="slidingExpiration">How long after each access the item may remain valid, or <c>null</c>.</param>
    /// <param name="absoluteExpiration">Hard expiry timestamp, or <c>null</c> for no absolute expiration.</param>
    public CachedItem(byte[] value, string? beforeCacheKey = null, string? afterCacheKey = null, TimeSpan? slidingExpiration = null, DateTimeOffset? absoluteExpiration = null)
    {
        Value = value;
        BeforeCacheKey = beforeCacheKey;
        AfterCacheKey = afterCacheKey;
        SlidingExpiration = slidingExpiration;
        AbsoluteExpiration = absoluteExpiration;
    }

    /// <summary>Raw cached bytes.</summary>
    public byte[] Value { get; private set; }

    /// <summary>Key of the preceding item in the LRU linked list, or <c>null</c> if this is the first item.</summary>
    public string? BeforeCacheKey { get; private set; }

    /// <summary>Key of the following item in the LRU linked list, or <c>null</c> if this is the last item.</summary>
    public string? AfterCacheKey { get; private set; }

    /// <summary>How long after each access the item may remain valid, or <c>null</c>.</summary>
    public TimeSpan? SlidingExpiration { get; private set; }

    /// <summary>Hard expiry timestamp, or <c>null</c> for no absolute expiration.</summary>
    public DateTimeOffset? AbsoluteExpiration { get; private set; }
}

class CachedItemSerializer : IStateSerializer<CachedItem>
{
    CachedItem IStateSerializer<CachedItem>.Read(BinaryReader reader)
    {
        var byteLength = reader.ReadInt32();
        return new CachedItem(
            reader.ReadBytes(byteLength),
            GetStringValueOrNull(reader.ReadString()),
            GetStringValueOrNull(reader.ReadString()),
            GetTimeSpanFromTicks(reader.ReadInt64()),
            GetDateTimeOffsetFromDateData(reader.ReadInt64(), reader.ReadInt64())
            );
    }

    void IStateSerializer<CachedItem>.Write(CachedItem value, BinaryWriter writer)
    {
        writer.Write(value.Value.Length);
        writer.Write(value.Value);
        writer.Write(value.BeforeCacheKey ?? string.Empty);
        writer.Write(value.AfterCacheKey ?? string.Empty);
        writer.Write(GetTicksFromTimeSpan(value.SlidingExpiration));
        writer.Write(GetLongDateTimeFromDateTimeOffset(value.AbsoluteExpiration));
        writer.Write(GetShortOffsetFromDateTimeOffset(value.AbsoluteExpiration));
    }

    // Read overload for differential de-serialization
    CachedItem IStateSerializer<CachedItem>.Read(CachedItem baseValue, BinaryReader reader)
    {
        return ((IStateSerializer<CachedItem>)this).Read(reader);
    }

    // Write overload for differential serialization
    void IStateSerializer<CachedItem>.Write(CachedItem baseValue, CachedItem newValue, BinaryWriter writer)
    {
        ((IStateSerializer<CachedItem>)this).Write(newValue, writer);
    }

    private static string? GetStringValueOrNull(string? value)
    {
        return value == string.Empty ? null : value;
    }

    private static TimeSpan? GetTimeSpanFromTicks(long ticks)
    {
        if (ticks == 0) return null;

        return TimeSpan.FromTicks(ticks);
    }

    private static long GetTicksFromTimeSpan(TimeSpan? timeSpan)
    {
        if (!timeSpan.HasValue) return 0;

        return timeSpan.Value.Ticks;
    }

    private static DateTimeOffset? GetDateTimeOffsetFromDateData(long dateDataTicks, long offsetTicks)
    {
        if (dateDataTicks == 0)
            return null;
        return new DateTimeOffset(DateTime.FromBinary(dateDataTicks), new TimeSpan(offsetTicks));
    }

    private static long GetLongDateTimeFromDateTimeOffset(DateTimeOffset? dateTimeOffset)
    {
        if (!dateTimeOffset.HasValue) return 0;
        return dateTimeOffset.Value.DateTime.ToBinary();
    }

    private static long GetShortOffsetFromDateTimeOffset(DateTimeOffset? dateTimeOffset)
    {
        if (!dateTimeOffset.HasValue) return 0;
        return dateTimeOffset.Value.Offset.Ticks;
    }
}

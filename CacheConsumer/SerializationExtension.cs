namespace CacheConsumer;

using MemoryPack;

/// <summary>
/// MemoryPack serialization helpers for converting objects to and from byte arrays.
/// </summary>
public static class SerializationExtension
{
    /// <summary>
    /// Serializes <paramref name="obj"/> to a MemoryPack byte array, or <c>null</c> when <paramref name="obj"/> is <c>null</c>.
    /// </summary>
    /// <typeparam name="T">The type of the object to serialize.</typeparam>
    /// <param name="obj">The object to serialize.</param>
    /// <returns>A MemoryPack byte array, or <c>null</c> if <paramref name="obj"/> is <c>null</c>.</returns>
    public static byte[]? ToByteArray<T>(this T obj)
    {
        if (obj is null)
        {
            return null;
        }

        return MemoryPackSerializer.Serialize(obj);
    }

    /// <summary>
    /// Deserializes a <typeparamref name="T"/> from <paramref name="byteArray"/> using MemoryPack,
    /// or <c>null</c> when <paramref name="byteArray"/> is <c>null</c>.
    /// </summary>
    /// <typeparam name="T">The type to deserialize into.</typeparam>
    /// <param name="byteArray">The byte array to deserialize.</param>
    /// <returns>The deserialized instance, or <c>null</c> if <paramref name="byteArray"/> is <c>null</c>.</returns>
    public static T? FromByteArray<T>(this byte[] byteArray) where T : class
    {
        if (byteArray == null)
        {
            return default;
        }

        return MemoryPackSerializer.Deserialize<T>(byteArray);
    }
}

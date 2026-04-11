namespace CacheConsumer;

using MemoryPack;

public static class SerializationExtension
{
    public static byte[] ToByteArray<T>(this T obj)
    {
        if (obj is null)
        {
            return null;
        }

        return MemoryPackSerializer.Serialize(obj);
    }

    public static T FromByteArray<T>(this byte[] byteArray) where T : class
    {
        if (byteArray == null)
        {
            return default;
        }

        return MemoryPackSerializer.Deserialize<T>(byteArray);
    }
}

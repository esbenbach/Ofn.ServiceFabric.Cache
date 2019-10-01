﻿namespace CacheConsumer
{
    using System.IO;
    using System.Runtime.Serialization.Formatters.Binary;

    public static class SerializationExtension
    {
        public static byte[] ToByteArray(this object obj)
        {
            if (obj == null)
            {
                return new byte[] { };
            }

            var binaryFormatter = new BinaryFormatter();
            using (var memoryStream = new MemoryStream())
            {
                binaryFormatter.Serialize(memoryStream, obj);
                return memoryStream.ToArray();
            }
        }

        public static T? FromByteArray<T>(this byte[] byteArray) where T : class
        {
            if (byteArray == null)
            {
                return default;
            }

            var binaryFormatter = new BinaryFormatter();
            using (var memoryStream = new MemoryStream(byteArray))
            {
                return binaryFormatter.Deserialize(memoryStream) as T;
            }
        }
    }
}

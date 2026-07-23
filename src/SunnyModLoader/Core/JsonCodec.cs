using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SunnyModLoader;

internal static class JsonCodec
{
    internal static T Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(json);
        using MemoryStream stream = new MemoryStream(bytes, writable: false);
        DataContractJsonSerializer serializer = CreateSerializer<T>();
        return (T)serializer.ReadObject(stream);
    }

    internal static string Serialize<T>(T value)
    {
        using MemoryStream stream = new MemoryStream();
        DataContractJsonSerializer serializer = CreateSerializer<T>();
        serializer.WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static DataContractJsonSerializer CreateSerializer<T>()
    {
        return new DataContractJsonSerializer(
            typeof(T),
            new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            });
    }
}

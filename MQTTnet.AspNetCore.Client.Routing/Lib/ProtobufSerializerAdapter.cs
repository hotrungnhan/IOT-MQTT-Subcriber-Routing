using MQTTnet.AspNetCore.Client.Routing.Interface;
using ProtoBuf;

namespace MQTTnet.AspNetCore.Client.Routing.Lib;

public class ProtobufSerializerAdapter : ISerializer
{
    public byte[] Serialize<T>(T obj)
    {
        var stream = new MemoryStream();
        Serializer.Serialize(stream, obj);
        return stream.ToArray();
    }

    public T? Deserialize<T>(byte[] data)
    {
        var s = Serializer.Deserialize<T>(data.AsMemory());
        return s;
    }
}
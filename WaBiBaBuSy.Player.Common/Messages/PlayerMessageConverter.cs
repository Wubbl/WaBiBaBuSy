using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WaBiBaBuSy.Player.Common.Messages;

/// <summary>
/// Custom JSON converter for PlayerMessageBase that deserializes to the correct concrete type
/// based on the MessageType discriminator property.
/// </summary>
public class PlayerMessageConverter : JsonConverter<PlayerMessageBase>
{
    public override PlayerMessageBase? ReadJson(JsonReader reader, Type objectType, PlayerMessageBase? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        var jsonObject = JObject.Load(reader);
        var messageType = jsonObject["MessageType"]?.Value<string>();

        PlayerMessageBase? message = messageType switch
        {
            "hwnd" => new PlayerMessageHwnd(),
            "loaded" => new PlayerMessageLoaded(),
            "cmd_load" => new PlayerCommandLoad(),
            "cmd_play" => new PlayerCommandPlay(),
            "cmd_close" => new PlayerCommandClose(),
            _ => null
        };

        if (message != null)
        {
            serializer.Populate(jsonObject.CreateReader(), message);
        }

        return message;
    }

    public override void WriteJson(JsonWriter writer, PlayerMessageBase? value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value);
    }
}

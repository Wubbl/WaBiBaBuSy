using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Content;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.Core.Services.Show;

/// <summary>
/// <see cref="ShowEvent"/> ↔ <see cref="SyncCommand"/> (type EVENT). The event's T0 travels in
/// <c>timestamp_utc</c> so the client's server→local clock conversion applies to it; the JSON's own T0 is ignored on
/// receipt. A Traversal image travels as a <see cref="ContentRef"/> in <c>params.assets</c>.
/// </summary>
public static class ShowEventCodec
{
    public static SyncCommand ToCommand(ShowEvent evt, int sequenceNumber, ContentAssetRef? asset = null)
    {
        var command = new SyncCommand
        {
            Type = CommandType.Event,
            TimestampUtc = evt.T0Utc,
            SequenceNumber = sequenceNumber,
            ContentId = asset?.ContentId ?? string.Empty,
            EventJson = JsonSerializer.Serialize(evt),
            Params = new SyncParameters(),
        };
        if (asset != null)
            command.Params.Assets.Add(new ContentRef { ContentId = asset.ContentId, Role = asset.Role, OriginalPath = asset.OriginalPath });
        return command;
    }

    /// <summary>The event of an EVENT command with T0 = the command's (converted) timestamp; false for anything else or a bad payload.</summary>
    public static bool TryFromCommand(SyncCommand command, [NotNullWhen(true)] out ShowEvent? evt)
    {
        evt = null;
        if (command.Type != CommandType.Event || string.IsNullOrWhiteSpace(command.EventJson)) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<ShowEvent>(command.EventJson);
            if (parsed == null || string.IsNullOrEmpty(parsed.EventId)) return false;
            parsed.T0Utc = command.TimestampUtc;
            evt = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

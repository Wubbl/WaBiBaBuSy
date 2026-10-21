using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Models.Show;

namespace WaBiBaBuSy.Core.Services.Show;

/// <summary>
/// <see cref="Agenda"/> ↔ <see cref="SyncCommand"/> (type AGENDA). The send time travels twice: in the JSON
/// (<see cref="Agenda.SentUtcMs"/>, server clock) and in <c>timestamp_utc</c>, which the client converts into its own
/// clock — the difference shifts every item, so "now / next" flips at the server's time on every seat.
/// </summary>
public static class AgendaCodec
{
    public static SyncCommand ToCommand(Agenda agenda, int sequenceNumber, long nowUtcMs)
    {
        var copy = JsonSerializer.Deserialize<Agenda>(JsonSerializer.Serialize(agenda))!;
        copy.SentUtcMs = nowUtcMs;
        return new SyncCommand
        {
            Type = CommandType.Agenda,
            TimestampUtc = nowUtcMs,
            SequenceNumber = sequenceNumber,
            AgendaJson = JsonSerializer.Serialize(copy),
        };
    }

    /// <summary>The agenda of an AGENDA command in this machine's clock; false for anything else or a bad payload.</summary>
    public static bool TryFromCommand(SyncCommand command, [NotNullWhen(true)] out Agenda? agenda)
    {
        agenda = null;
        if (command.Type != CommandType.Agenda || string.IsNullOrWhiteSpace(command.AgendaJson)) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<Agenda>(command.AgendaJson);
            if (parsed == null) return false;
            if (parsed.SentUtcMs != 0 && command.TimestampUtc != 0)
                AgendaMath.ShiftTimes(parsed, command.TimestampUtc - parsed.SentUtcMs);
            agenda = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

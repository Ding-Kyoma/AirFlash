using System.Text.Json;
namespace AirFlash.Core;

// Runtime receiver state. Never persisted or used as a PCM gain.
public sealed record DeviceVolumeState(int? Actual = null, int? Target = null, long Sequence = 0,
    bool Available = false, string Status = "unknown", int? LastNonZero = null, bool ReadAfterWrite = false)
{
    public int? Display => Target ?? Actual;
    public DeviceVolumeState Begin(int value, long sequence) => this with
    { Target = value, Sequence = sequence, Status = "pending", ReadAfterWrite = false };
    public DeviceVolumeState Receive(JsonElement item)
    {
        if (item.Integer("sequence") != Sequence) return this;
        var number = item.Number("volume");
        int? actual = number is >= 0 and <= 100 && double.IsFinite(number.Value) && number == Math.Truncate(number.Value) ? (int)number.Value : null;
        var available = item.Boolean("available") == true && actual is not null;
        var status = item.Text("status");
        if (status is not ("pending" or "confirmed" or "unconfirmed" or "unsynced")) return this;
        // A late pending event cannot resurrect a target already timed out locally.
        if (Target is null && status == "pending") status = "unconfirmed";
        return this with {
            Actual = actual ?? Actual, Available = available, Status = status,
            Target = status == "pending" ? Target : null,
            LastNonZero = actual is > 0 ? actual : LastNonZero,
            ReadAfterWrite = ReadAfterWrite || actual is not null
        };
    }
    public DeviceVolumeState Timeout() => this with
    { Target = null, Available = Available && ReadAfterWrite, Status = "unconfirmed" };
}

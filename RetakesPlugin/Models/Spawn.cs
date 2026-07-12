using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Utils;

using RetakesPlugin.Configs.JsonConverters;
using RetakesPluginShared.Enums;

namespace RetakesPlugin.Models;

public class Spawn
{
    public Spawn(Vector vector, QAngle qAngle)
    {
        Vector = vector;
        QAngle = qAngle;
    }

    [JsonConverter(typeof(VectorJsonConverter))]
    public Vector Vector { get; }

    [JsonConverter(typeof(QAngleJsonConverter))]
    public QAngle QAngle { get; }

    public CsTeam Team { get; set; }
    public Bombsite Bombsite { get; set; }
    public bool CanBePlanter { get; set; }

    /// <summary>
    /// Garden (ROADMAP R1): mode flags consumed by later phases —
    /// "duel" (R4), "smallserver" (R5), "execute" (R6/R7). Backward compatible:
    /// old map JSONs simply deserialize with an empty list.
    /// </summary>
    [JsonPropertyName("Flags")]
    public List<string> Flags { get; set; } = [];

    /// <summary>Garden (ROADMAP R1): who placed this spawn (multi-editor attribution).</summary>
    [JsonPropertyName("AddedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AddedBy { get; set; }
}
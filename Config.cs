using System.Collections.Generic;
using Dalamud.Configuration;

namespace Begone;

// Persisted state. Hides are remembered across zone loads, plugin reloads, and sessions.
//
// The identity we persist is (TerritoryId, DataId), NOT ObjectIndex. ObjectIndex is a slot in the live
// object table — it is reassigned as objects stream in and out and is meaningless across a zone load. An
// EventNpc's DataId (its ENpcResident row) is stable: the same NPC standing in the same place has the same
// DataId every visit, on every character. That is what makes "remember what I hid here" well-defined.
//
// TerritoryType is a ushort (IClientState.TerritoryType) so we key on ushort. DataId is a uint.
//
// We cache display strings alongside the ids purely so the inventory reads nicely when you are NOT currently
// standing in that map (we can't resolve a live Name for a zone we aren't in). They are cosmetic.

public sealed class MapEntry
{
    public string MapName = "";                    // resolved TerritoryType place name, cached for the inventory
    public HashSet<uint> HiddenDataIds = new();     // ENpc DataIds hidden on this map
    public Dictionary<uint, string> Names = new();  // dataId -> last-seen NPC name (cosmetic, for the list)
}

public sealed class Config : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Master switch for the world overlay ("dot lens"). Off by default: opening the plugin shows only the
    // menu + inventory; you opt into the clickable dots. Remembered like every other setting.
    public bool LensEnabled = false;

    // territoryId (uint) -> what's hidden there.
    public Dictionary<uint, MapEntry> Maps = new();

    public void Save() => Services.PluginInterface.SavePluginConfig(this);

    public static Config Load()
        => Services.PluginInterface.GetPluginConfig() as Config ?? new Config();
}

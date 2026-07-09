using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;

namespace Begone;

// Begone — a cosmetic QoL plugin. Enable the lens, click the dot over an event NPC to hide its APPEARANCE;
// click again to restore. Collision is NEVER touched (you can't fall through or hit invisible walls). Only
// NPCs the game is ACTUALLY rendering get a dot — quest-locked "phantom" NPCs (present in the object table
// but not drawn) never appear, by design. Only EventNpcs can be hidden — mobs (BattleNpc) are never targetable.
//
// The hide lever is RenderFlags |= VisibilityFlags.Model (== 1<<1 == 0x02) — the model/appearance render-gate
// that leaves the DrawObject intact (Penumbra-safe; it does NOT destroy the draw object the way DisableDraw
// would). Marker (NamePlateIconId) is zeroed too so a hidden NPC leaves no floating quest icon.
//
// Hides are REMEMBERED. They persist across zone loads, reloads, and sessions, keyed by (TerritoryId, BaseId)
// — never ObjectIndex, which is a transient table slot. On entering a map we re-apply the remembered hides to
// the matching live NPCs; the inventory lists every map with hidden NPCs and how many. On unload we restore
// the live session cleanly (RenderFlags cleared), but the config remembers, so a reload re-hides.
public sealed unsafe class Plugin : IDalamudPlugin
{
    private readonly WindowSystem _windows = new("Begone");
    private readonly BegoneWindow _window;

    public Plugin(IDalamudPluginInterface pi)
    {
        Services.Init(pi);

        var config = Config.Load();
        _window = new BegoneWindow(config);
        _windows.AddWindow(_window);

        Services.PluginInterface.UiBuilder.Draw += _window.Tick;      // always: keep remembered hides applied + count live
        Services.PluginInterface.UiBuilder.Draw += _windows.Draw;
        Services.PluginInterface.UiBuilder.Draw += _window.DrawOverlay;
        Services.PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        Services.PluginInterface.UiBuilder.OpenConfigUi += OpenMain;
        Services.ClientState.TerritoryChanged += _window.OnTerritoryChanged;

        Services.CommandManager.AddHandler("/begone", new Dalamud.Game.Command.CommandInfo((_, _) => OpenMain())
        {
            HelpMessage = "Open the Begone window. Enable the lens there to click-hide NPCs."
        });
    }

    private void OpenMain() => _window.IsOpen = !_window.IsOpen;

    public void Dispose()
    {
        Services.ClientState.TerritoryChanged -= _window.OnTerritoryChanged;
        _window.RestoreLiveSession();   // clear RenderFlags on everything we hid this session; config still remembers
        Services.CommandManager.RemoveHandler("/begone");
        Services.PluginInterface.UiBuilder.Draw -= _window.Tick;
        Services.PluginInterface.UiBuilder.Draw -= _windows.Draw;
        Services.PluginInterface.UiBuilder.Draw -= _window.DrawOverlay;
        Services.PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        Services.PluginInterface.UiBuilder.OpenConfigUi -= OpenMain;
        _windows.RemoveAllWindows();
    }
}

public sealed unsafe class BegoneWindow : Window
{
    private readonly Config _config;

    private struct NpcRow
    {
        public nint Addr;
        public ushort Index;
        public uint DataId;
        public Vector3 Center;
        public float Dist;
        public string Name;
        public bool Hidden;
    }

    private readonly List<NpcRow> _rows = new();
    private nint _hoveredAddr = 0;

    // How many NPCs are in range right now (visible + our-hidden), recomputed each gather for the count line.
    private int _inRange;

    // The hide bit. VisibilityFlags.Model == (1 << 1) == 0x02 — the model/appearance render-gate. Using the
    // named enum member (not a raw 0x02) keeps it correct across CS updates and self-documents intent.
    private const VisibilityFlags HideBit = VisibilityFlags.Model;

    private const uint ColEvent = 0xFF30C0FFu;   // amber — a hideable EventNpc
    private const uint ColHidden = 0xFF808080u;  // grey — currently hidden
    private const uint ColBlack = 0xFF000000u;
    private const uint ColGreen = 0xFF40FF40u;
    private const uint ColWhite = 0xFFFFFFFFu;

    public BegoneWindow(Config config) : base("Begone!###begone_main")
    {
        _config = config;
        Size = new Vector2(380, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    // ---- persistence helpers -------------------------------------------------------------

    private uint Territory => Services.ClientState.TerritoryType;

    private MapEntry MapFor(uint territory, bool create)
    {
        if (_config.Maps.TryGetValue(territory, out var e)) return e;
        if (!create) return null!;
        e = new MapEntry { MapName = ResolveMapName(territory) };
        _config.Maps[territory] = e;
        return e;
    }

    private static string ResolveMapName(uint territory)
    {
        var fallback = $"Territory {territory}";
        var sheet = Services.DataManager.GetExcelSheet<TerritoryType>();
        if (sheet is null) return fallback;
        if (!sheet.TryGetRow(territory, out var row)) return fallback;
        var place = row.PlaceName.ValueNullable?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(place) ? fallback : place!;
    }

    private int TotalHiddenEverywhere() => _config.Maps.Values.Sum(m => m.HiddenDataIds.Count);
    private int HiddenHereCount()
        => _config.Maps.TryGetValue(Territory, out var e) ? e.HiddenDataIds.Count : 0;

    // ---- visibility lever ----------------------------------------------------------------

    // Is the game ACTUALLY drawing this NPC? DrawObject non-null AND IsVisible. Quest-locked NPCs sit in the
    // object table with a null/invisible draw object — those are the "phantoms" we never show. An NPC WE hid
    // also reports not-rendered; the gather treats "we hid it" as still-listable so it stays clickable.
    private static bool IsRendered(GameObject* native)
    {
        if (native == null) return false;
        var draw = native->DrawObject;
        return draw != null && draw->IsVisible;
    }

    // Push the hide onto a live object (idempotent). Does not touch config.
    private static void ApplyHide(GameObject* native)
    {
        if (native == null) return;
        if (native->NamePlateIconId != 0) native->NamePlateIconId = 0;
        native->RenderFlags |= HideBit;   // appearance-only; collision untouched
    }

    private static void ClearHide(GameObject* native)
    {
        if (native == null) return;
        native->RenderFlags &= ~HideBit;
    }

    // User clicked a visible NPC's dot: remember it, and hide it live.
    private void HideNpc(ushort idx, uint dataId, string name)
    {
        var map = MapFor(Territory, create: true);
        map.HiddenDataIds.Add(dataId);
        map.Names[dataId] = name;
        var obj = Services.ObjectTable[(int)idx];
        if (obj != null) ApplyHide((GameObject*)obj.Address);
        _config.Save();
    }

    // User clicked a hidden NPC's dot: forget it, and show it live.
    private void ShowNpc(ushort idx, uint dataId)
    {
        var obj = Services.ObjectTable[(int)idx];
        if (obj != null) ClearHide((GameObject*)obj.Address);
        if (_config.Maps.TryGetValue(Territory, out var map))
        {
            map.HiddenDataIds.Remove(dataId);
            map.Names.Remove(dataId);
            if (map.HiddenDataIds.Count == 0) _config.Maps.Remove(Territory);
        }
        _config.Save();
    }

    // Restore every NPC hidden on ONE map. Clears live objects if that map is the current one, then forgets.
    private void RestoreMap(uint territory)
    {
        if (territory == Territory)
        {
            foreach (var obj in Services.ObjectTable)
            {
                var native = (GameObject*)obj.Address;
                if (native == null) continue;
                if (native->ObjectKind != ObjectKind.EventNpc) continue;
                ClearHide(native);
            }
        }
        _config.Maps.Remove(territory);
        _config.Save();
    }

    // Restore EVERYTHING across all maps. Clears the live current map, then wipes the whole inventory.
    private void RestoreEverything()
    {
        RestoreLiveSession();   // clear live objects on the current map
        _config.Maps.Clear();
        _config.Save();
    }

    // Clear RenderFlags on every EventNpc currently in the live table that we have marked hidden here.
    // Used on unload and as the live half of a full restore. Leaves config untouched (caller decides).
    public void RestoreLiveSession()
    {
        foreach (var obj in Services.ObjectTable)
        {
            var native = (GameObject*)obj.Address;
            if (native == null) continue;
            if (native->ObjectKind != ObjectKind.EventNpc) continue;
            ClearHide(native);
        }
    }

    // On zone change, live objects are rebuilt fresh by the game. We do NOT drop tracking (it's persisted per
    // territory). Tick() runs every frame and re-applies the remembered hides for whatever territory we're now
    // in, so there's nothing to do here — but we keep the hook subscribed as the documented seam for this.
    public void OnTerritoryChanged(uint territory)
    {
    }

    // ---- the per-frame object walk -------------------------------------------------------
    //
    // One walk of the object table, shared by two callers. It ALWAYS re-applies remembered hides to live
    // EventNpcs and recomputes the in-range count — this runs every frame regardless of the lens or whether
    // the window is open, so a remembered hide takes effect the moment you enter a map, and the count line is
    // always live. When buildRows is true (the overlay), it also builds the clickable-dot list.
    //
    // Runs on the framework thread (UiBuilder.Draw), which is the only safe place to touch the object table.
    private void WalkObjects(bool buildRows)
    {
        if (buildRows) _rows.Clear();
        _inRange = 0;

        if (Services.ObjectTable.LocalPlayer is null) return;

        var map = _config.Maps.TryGetValue(Territory, out var e) ? e : null;
        var origin = Services.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;

        foreach (var obj in Services.ObjectTable)
        {
            var native = (GameObject*)obj.Address;
            if (native == null) continue;

            // EventNpc only. Mobs (BattleNpc) and everything else are never hideable and never get a dot.
            if (native->ObjectKind != ObjectKind.EventNpc) continue;

            var dataId = obj.BaseId;
            bool hidden = map != null && map.HiddenDataIds.Contains(dataId);

            // Re-apply the remembered hide to the live object every frame: late-streaming NPCs get the same
            // treatment, and any flag/marker the game reset gets re-asserted. This is why a remembered hide
            // "sticks" across a zone load even with the lens off.
            if (hidden) ApplyHide(native);

            // Phantom filter (always on — the core behaviour): count/show only NPCs the game is drawing, OR
            // ones we hid (so they stay clickable to un-hide). Quest-locked phantoms are skipped entirely.
            // No distance gate — we ride the game's natural culling: if it's in the table and drawn, it counts.
            if (!hidden && !IsRendered(native)) continue;

            _inRange++;

            if (hidden && map != null)
            {
                var live = obj.Name.ToString();
                if (!string.IsNullOrEmpty(live)) map.Names[dataId] = live;   // keep cached name fresh while visible
            }

            if (!buildRows) continue;

            var idx = (ushort)obj.ObjectIndex;
            var name = obj.Name.ToString();
            if (string.IsNullOrEmpty(name))
                name = (hidden && map != null && map.Names.TryGetValue(dataId, out var cached)) ? cached : "EventNpc #" + idx;

            var pos = obj.Position;
            _rows.Add(new NpcRow
            {
                Addr = obj.Address,
                Index = idx,
                DataId = dataId,
                Center = pos,
                Dist = Vector3.Distance(pos, origin),
                Name = name,
                Hidden = hidden,
            });
        }

        if (buildRows) _rows.Sort((a, b) => a.Dist.CompareTo(b.Dist));
    }

    // Runs every frame from UiBuilder.Draw, independent of the window/lens: keeps remembered hides applied and
    // the count fresh. Cheap (one object-table pass). Bails immediately if nothing is hidden here and the
    // window is closed, so idle sessions pay almost nothing.
    public void Tick()
    {
        bool anythingHere = _config.Maps.TryGetValue(Territory, out var e) && e.HiddenDataIds.Count > 0;
        if (!anythingHere && !IsOpen) { _inRange = 0; return; }
        WalkObjects(buildRows: false);
    }

    // ---- main window ---------------------------------------------------------------------

    public override void Draw()
    {
        ImGui.TextUnformatted("Begone!");
        ImGui.SameLine();
        ImGui.TextDisabled("Hide NPCs in the world with a single click.");

        ImGui.Separator();

        // Lens toggle — the master switch for the world overlay. Off => window/inventory only.
        bool lens = _config.LensEnabled;
        if (ImGui.Checkbox("Enable", ref lens))
        {
            _config.LensEnabled = lens;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show a clickable dot over every visible NPC in the world.\nClick a dot to hide that NPC; click again to bring it back.");

        ImGui.Spacing();
        ImGui.TextWrapped("Click to hide any NPC. Click again to display. Cosmetic and client-side only, everyone else still sees them. Quest hidden NPCs will lose the quest indicator when revealed until you talk to them.");

        ImGui.Separator();

        if (Services.ObjectTable.LocalPlayer is null)
        {
            ImGui.TextDisabled("Not in game.");
            DrawInventory();
            return;
        }

        // Count line: in-range now, hidden here, total hidden everywhere.
        int here = HiddenHereCount();
        int total = TotalHiddenEverywhere();
        ImGui.TextDisabled($"{_inRange} NPCs in range   ·   {here} hidden here   ·   {total} hidden total");

        if (here > 0)
        {
            if (ImGui.Button($"Restore all on this map ({here})"))
                RestoreMap(Territory);
        }

        DrawInventory();
    }

    private void DrawInventory()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Hidden NPCs by map");

        if (_config.Maps.Count == 0)
        {
            ImGui.TextDisabled("Nothing hidden yet.");
            return;
        }

        // master restore
        if (ImGui.Button("Restore everything"))
            RestoreEverything();

        ImGui.Spacing();

        if (ImGui.BeginTable("begone_maps", 3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Hidden", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableHeadersRow();

            // stable order for a clean list
            foreach (var kv in _config.Maps.OrderBy(m => m.Value.MapName))
            {
                var territory = kv.Key;
                var entry = kv.Value;
                if (entry.HiddenDataIds.Count == 0) continue;

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                string label = string.IsNullOrEmpty(entry.MapName) ? $"Territory {territory}" : entry.MapName;
                bool current = territory == Territory;
                ImGui.TextUnformatted(current ? label + "  (here)" : label);
                ImGui.TextDisabled($"ID {territory}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.HiddenDataIds.Count.ToString());

                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Restore###restore_{territory}"))
                    RestoreMap(territory);
            }

            ImGui.EndTable();
        }
    }

    // ---- world overlay: dots + click-to-toggle -------------------------------------------

    public void DrawOverlay()
    {
        if (!IsOpen) return;
        if (!_config.LensEnabled) return;   // lens is the master switch for the dots
        if (Services.ObjectTable.LocalPlayer is null) return;

        WalkObjects(buildRows: true);

        var dl = ImGui.GetBackgroundDrawList();
        var mouse = ImGui.GetMousePos();

        // hover pass
        _hoveredAddr = 0;
        float best = float.MaxValue;
        foreach (var r in _rows)
        {
            if (!WorldToScreen(r.Center, out var sp)) continue;
            float d = Vector2.Distance(mouse, sp);
            if (d < 12f && d < best) { best = d; _hoveredAddr = r.Addr; }
        }

        // click detection. World dots live OUTSIDE the plugin window, so the correct gate is "cursor not over
        // any ImGui window" (IsWindowHovered(AnyWindow)), NOT WantCaptureMouse (which is true whenever the
        // cursor is over our panel and would block every click). When a dot is hovered, claim the click via
        // SetNextFrameWantCaptureMouse so the game doesn't also act on it.
        ushort clickIdx = 0; uint clickData = 0; string clickName = ""; bool doHide = false, doShow = false;
        if (_hoveredAddr != 0 && !ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow))
        {
            ImGui.SetNextFrameWantCaptureMouse(true);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                foreach (var r in _rows)
                {
                    if (r.Addr != _hoveredAddr) continue;
                    clickIdx = r.Index; clickData = r.DataId; clickName = r.Name;
                    if (r.Hidden) doShow = true; else doHide = true;
                    break;
                }
            }
        }

        // draw pass
        foreach (var r in _rows)
        {
            if (!WorldToScreen(r.Center, out var sp)) continue;
            bool hov = r.Addr == _hoveredAddr;
            float distT = Math.Clamp((r.Dist - 6f) / (80f - 6f), 0f, 1f);
            float fade = 1f - MathF.Pow(distT, 0.55f) * 0.7f;   // near bright, far faint (matches feel)
            uint baseCol = r.Hidden ? ColHidden : ColEvent;
            uint c = ScaleAlpha(baseCol, fade);
            dl.AddCircleFilled(sp, hov ? 7f : 5f, c);
            dl.AddCircle(sp, (hov ? 7f : 5f) + 0.5f, ColBlack, 0, 1.5f);
            if (hov)
            {
                dl.AddCircle(sp, 11f, ColGreen, 0, 2.5f);
                dl.AddText(sp + new Vector2(13f, -6f), ColWhite, r.Name + (r.Hidden ? "  (click to show)" : "  (click to hide)"));
            }
        }

        // apply the click AFTER the draw loop (don't mutate mid-enumerate).
        if (doHide) HideNpc(clickIdx, clickData, clickName);
        else if (doShow) ShowNpc(clickIdx, clickData);
    }

    private static bool WorldToScreen(Vector3 world, out Vector2 screen)
        => Services.GameGui.WorldToScreen(world, out screen);

    private static uint ScaleAlpha(uint abgr, float f)
    {
        byte a = (byte)((abgr >> 24) & 0xFF);
        a = (byte)Math.Clamp(a * f, 0f, 255f);
        return (abgr & 0x00FFFFFFu) | ((uint)a << 24);
    }
}

# Begone

**Out of sight, out of mind.** Hide NPCs in the world with a single click: a cosmetic quality-of-life
plugin for FFXIV (Dalamud). For screenshots, roleplay, or just a cleaner scene.

## What it does
- Open the window (`/begone`) and tick **Enable**. A small dot appears over every visible NPC near you.
- **Click a dot to hide that NPC. Click it again to bring them back.**
- Click to hide any NPC. Click again to display. Cosmetic and client-side only, everyone else still sees them.
  Quest hidden NPCs will lose the quest indicator when revealed until you talk to them.
- Hides **appearance only**: collision is never touched, so you'll never fall through the floor or bump into
  an invisible NPC where a visible one used to be.
- Only shows dots for NPCs your character can **actually see**. Quest-locked NPCs that the game hasn't unlocked
  for you (they exist in memory but aren't drawn) never get a dot, so no phantom clutter.
- Only **event NPCs** can be hidden. Mobs (BattleNpcs: dungeon/field enemies, striking dummies, summons) are
  never targeted and never get a dot.

## Remembered, with an inventory
- Your hides are **remembered** across zone changes, plugin reloads, and sessions. Re-enter a map and the NPCs
  you hid there are hidden again automatically (you don't need the lens on for this).
- The window shows a **count** (NPCs in range, hidden here, hidden total) and an **inventory of maps**: each map
  where you've hidden NPCs, how many, and a per-map **Restore** button.
- **Restore all on this map** brings back everything hidden on your current map; **Restore everything** clears the
  whole inventory across all maps.
- Opening the plugin on its own just shows the window and inventory; the world dots only appear once you enable
  the lens.

## Safe by design
- **Client-side and cosmetic.** Nothing about hiding is sent to the server. Other players always see the NPC.
- The hide is a render-flag toggle (`VisibilityFlags.Model`) that leaves the NPC's draw object intact
  (Penumbra-safe): it does not destroy or respawn anything, and does not touch collision or targeting geometry.
- On unload, Begone clears the render flag on everything it hid this session (your hide list is still remembered
  in config, so a reload re-hides).

## Usage
- `/begone`: open/close the window.
- Tick **Enable**, then click any dot in the world to hide/show that NPC.
- Use the inventory's **Restore** buttons, **Restore all on this map**, or **Restore everything** to bring NPCs back.

## Notes for developers
The hide lever is `GameObject.RenderFlags |= VisibilityFlags.Model` (`1 << 1`, i.e. `0x02`, the model/appearance
render-gate that preserves the DrawObject), plus zeroing `NamePlateIconId` so no floating quest marker is left
behind. Persistence is keyed by **(TerritoryType, BaseId)**, never ObjectIndex, which is a transient object-table
slot that re-indexes across zone loads; an event NPC's BaseId (the model/appearance id, formerly `DataId`) is
stable per NPC per zone. A once-per-frame pass
(`UiBuilder.Draw`, framework thread) re-applies remembered hides to live event NPCs and keeps the count fresh,
independent of whether the window or lens is open. The "visible only" filter is `DrawObject != null &&
DrawObject->IsVisible`. All object-table access runs on the framework draw callback against the live table, with no
cached pointers held across frames.

## License

AGPL-3.0. See [LICENSE](LICENSE). You may use, modify, and redistribute this
freely; derivative and network-served versions must stay open under the same
license.

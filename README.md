# DigiTeam Valheim mods

The source code of DigiTeam's BepInEx mods for Valheim, published for reading and reviewing. Download the mods from
their official pages: [Thunderstore](https://thunderstore.io/c/valheim/p/DigiTeam/) or
[Hexium](https://valheim.hexium.gg) (team DigiTeam). Questions, bugs and ideas:
[our Discord](https://discord.gg/haSwxPa9GU).

## What is here
- `combined/`: the combined mods, each one plugin with many sub-mods that can be switched on and off in F1
  ("0 - Sub-mods"): Crafting and Automation (`DigiCrafting`), Building Tweaks (`DigiBuilding`), Combat and Loot
  (`DigiCombat`), Epic Loot Addons (`DigiEpicLoot`), World and Performance (`DigiWorld`), Inventory and QoL (`DigiQoL`).
  Each sub-mod lives in `Modules/<Name>/`; its file header says what it does and why.
- `standalone/`: Auto Map Pins (`DigiMapPins`), Planting Tools (`DigiPlanting`), Portal List (`DigiPortalList`),
  Ultimate Builder (`DigiCreative`).
- `core/`: code shared by the mods (`DigiCore`: the sub-mod switches and settings; `DigiShared`, `DigiStorage`).

## Building
.NET SDK 8 and a Valheim install with BepInEx. Each project expects the game in its `ValheimDir`
(`C:\Program Files (x86)\Steam\steamapps\common\Valheim`); Epic Loot Addons also needs Epic Loot 0.15.6 in the
game's `BepInEx\plugins`. Then `dotnet build -c Release` in a mod's folder.

## License
All rights reserved: see [LICENSE](LICENSE). Reading and reviewing are welcome; please do not re-upload the mods or
builds of this code.

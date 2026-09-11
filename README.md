# QuickStack

A BepInEx mod for Valheim that quick-stacks matching items from your inventory into nearby containers, with slot/item favoriting to protect items you don't want moved.

<img width="800" height="449" alt="quickstackDemo" src="https://github.com/user-attachments/assets/a4eabf57-8ef6-4537-ae83-536343bac4f7" />

## Features

- Quick-stack matching items into all containers within a configurable range
- Favorite specific item identities (type + quality + variant) via Alt+Left-click
- Favorite specific inventory slots (position-based, independent of item) via Alt+Right-click
- Configurable hotkey, search range, and equipped/hotbar exclusion
- Basic multiplayer safety checks (see below)

## How it works

`RunQuickStack()` runs on hotkey press. It reads the player's inventory and iterates a cached `HashSet<Container>` of all containers currently loaded on the client (populated/pruned via Harmony postfixes on `Container.Awake` / `Container.OnDestroyed`, so no per-frame `FindObjectsOfType` scans). For each container within range, it walks the player's items and moves any item whose name matches an existing stack in that container, using `Inventory.MoveItemToThis`. Two categories of items are protected from being moved: standing exclusions (equipped items, hotbar items) and user-set favorites (slot-based or identity-based).

### Favoriting

Two independent favorite systems, applied on top of each other:

- **Identity favorites** (`favoriteItemIdentities`, blue border): keyed by `"{m_name}|{m_quality}|{m_variant}"`, so the item is protected no matter where it moves in the inventory or how many are picked up.
- **Slot favorites** (`favoriteSlots`, gold border): keyed by `Vector2i` grid position, protecting whatever occupies that slot regardless of item identity.

Both are persisted to the BepInEx config file as delimited strings (`x:y` pairs for slots, `|`-delimited identity strings for items) and reloaded on `Awake`. Borders are UI-only decoration added to `InventoryElement` GameObjects and are reapplied every frame in `Update()` since Valheim recreates inventory UI elements on refresh/reopen.

Alt-click detection hooks into `InventoryGrid.OnLeftClick`, `InventoryGrid.OnLeftRelease`, `InventoryGui.OnSelectedItem`, and `InventoryGui.OnRightClickItem` with `Prefix` patches that return `false` (suppressing the vanilla pickup/use behavior) whenever Alt is held, so favoriting doesn't also trigger normal inventory interaction.

### Multiplayer safety

This mod checks `Container.IsInUse()` before scanning a container and again immediately before/during item transfer, to avoid moving items into or out of a container another player currently has open. This is a local, client-side check against synced container state — it is **not** a network round-trip, so it can't guarantee correctness under latency or simultaneous access from two clients that both pass the check in the same tick. Treat it as a mitigation, not a hard guarantee.

Ship containers are excluded from quick-stacking by default, toggleable via the `Skip ship containers` config option. Ships get opened and closed in rapid succession by multiple players (e.g. everyone loading up before setting sail), which makes them a higher-risk case for the same class of desync/duping issues `IsInUse()` is meant to catch. Note that `Container` and `Ship` are unrelated component types in Valheim — ship cargo is a `Container` child object on the ship, not a `Container` subtype of `Ship` — so detection is done via `GetComponentInParent<Ship>()`, not a type check.

**Multiplayer behavior has not been extensively tested.** Feedback from multiplayer sessions is welcome.

## Installation

1. Install [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) for Valheim.
2. Drop `QuickStack.dll` into `Valheim/BepInEx/plugins/`.
3. Launch the game once to generate the config file.

## Configuration

Config file: `Valheim/BepInEx/config/quickstack.cfg`

| Setting | Default | Description |
|---|---|---|
| `Quick stack hotkey` | `BackQuote` | Key to trigger quick-stacking |
| `Container range` | `20` | Max distance (m) to search for containers |
| `Skip equipped items` | `true` | Exclude currently equipped items |
| `Skip hotbar items` | `true` | Exclude items assigned to the hotbar |
| `Skip ship containers` | `true` | Exclude ship storage from quick-stacking |

`[Favorites]` entries (`Slots`, `Items`) are written automatically by the mod and shouldn't be hand-edited unless you know the coordinate/identity format.

## Building from source

Requires .NET SDK, and Valheim + BepInEx installed locally (for `assembly_valheim.dll`, `0Harmony.dll`, `BepInEx.dll`, `UnityEngine*.dll` references — see `QuickStack.csproj`).

```
git clone https://github.com/MMaxG/valheim-quickstack-mod.git
cd valheim-quickstack-mod/QuickStack
dotnet build
```

Output DLL: `bin/Debug/netstandard2.1/QuickStack.dll`. Copy into `BepInEx/plugins/`.

## Media

Item favoriting (Alt+Left-click):

<img width="800" height="994" alt="leftclickdemo" src="https://github.com/user-attachments/assets/99b41d96-a82c-4299-b496-3699ad065c8b" />


Slot favoriting (Alt+Right-click):

<img width="800" height="994" alt="rightclickdemo" src="https://github.com/user-attachments/assets/2889d627-5bd9-4577-a396-68acfa473aaa" />

## Known limitations

- `IsInUse()` checks are local/client-side, not network-synced — not fully reliable under latency or true simultaneous access.
- No dedicated compatibility handling for other inventory mods yet.

## Contributing

Issues and PRs welcome. Please include repro steps for any multiplayer-related bug reports (host/client, item type, container type) where possible — these are the hardest to track down without a dedicated test setup.

## License

*(add your license here)*

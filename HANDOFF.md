# Leaves-TreasureBox — Handoff

Last updated: 2026-07-15. Read `4.0 Mods\HANDOFF.md` first for project-wide rules.

---

## 1. What the mod does

A grab-bag of ~18 small, personal tweaks, each independently toggled in config. No unifying
theme — it's the author's "stuff I wanted for myself" bin. Single class, `src/TreasureBox.cs`
(~580 lines); `OnLoad` is just a flat list of `if (config.X) DoX();` calls.

## 2. Lifecycle

`[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]`, `IOnLoad` only — no periodic
behavior, so no Singleton/double-construction concern. Uses the obsolete `ConfigServer`
(CS0618, suppressed) to reach TraderConfig/QuestConfig/InventoryConfig — same SPT-4.1 blocker
noted elsewhere.

## 3. The tweaks (each is one private method, gated by one config bool)

| Toggle | What it does |
|---|---|
| `GLUCK` | Copies every Glock 17 slot filter onto the Glock 18C so 17 mods fit the 18C. |
| `M14` | Gives the M1A full-auto (725 rpm, single/fullauto). |
| `GrenadeLauncherErgo` | Sets ergo on all UBGLs to `grenadeLauncherErgoValue`. **Bug fixed in port:** TS hardcoded -10 ignoring the config; now uses the configured value (no-op at the -10 default). |
| `UMPDrum` | Clones the M4 drum into a .45 D-60 mag (fixed id `6773b8832c3a1a1c5e32af68`), fits it to UMP (+ Vector, + traders, per sub-toggles). |
| `ZeroToHeroPouch` | Swaps the Alpha container for a Pouch in the Zero-to-Hero starting profile. |
| `RagfairLevelOverride` | Sets flea min user level to `ragFairMinUserLevel`. |
| `AK12Muzzles` | Adds AK-105 muzzle-device filters to the AK-12. |
| `FuckFakeVodka` | Renames the fake vodka item via a **locale transformer** (§4). |
| `RpdMods` | Adds AK-103 muzzles + gas-tube handguards to both RPDs. |
| `MemeUSP45` | Adds one specific mod to the USP .45. |
| `EnableTraderRefreshAdjustment` | Shifts all trader refresh times by min/max, clamped to a floor. |
| `EnableSuppressorErgoAdjustment` | Adds a flat ergo delta to every suppressor. |
| `M700Barrel` | Adds an SA-58 barrel to the M700 and AKMN muzzles to the SVD barrel. |
| `EnableQuests` | Un-gates some event quests + reconfigures CASE / sealed-CASE random-loot containers. |
| `Junkbox` | Sets Therapist's 3.10/3.11 junkbox barter counts to 80. |
| `BTRChanges` | Forces BTR to always spawn (Streets + Woods). |
| `KeyChanges` | Gives mechanical keys/keycards charges (40→unlimited, 10→5 uses). |
| `BitcoinPrice` | Cuts the handbook Bitcoin price to a third. |
| `EnableFenceBlacklist` | Adds `fenceBlacklist` ids to Fence's blacklist. |
| `airdropWeightOverride` | Overrides airdrop **type** weightings (`AirdropConfig.AirdropTypeWeightings`) at runtime from `airdropWeights` (string type-name → relative weight), so base `airdrop.json` stays untouched. Only listed types change; unknown names skipped with a warning. Default raises `foodMedical`+`barter` 100→300. |

Most are simple slot-filter or property edits via `leavesUtils.getSlotFilter(item, N).Add(...)`
(positional slot access — see LeavesUtil handoff §3.3) or direct `itemDB[id].Properties`
assignment. All the hardcoded MongoIds are game item ids; they're local `const`/inline because
each tweak is self-contained.

## 4. The one cross-mod-relevant piece: `FakeVodkaFix` (locale transformer)

This is the concrete reason LeavesUtils clears its locale cache at the final load stage. The
fix renames item `614451b71e5874611e2c7ae5` by registering an `AddTransformer` on the `en`
global locale. A direct write to `LazyLoad.Value` would be silently discarded (re-deserialized
from disk each access); the transformer is what persists. And because this transformer is
registered during TreasureBox's OnLoad, any mod that read the locale *earlier* in load would
have cached a pre-rename copy — which is exactly what LeavesUtils' one-time cache clear (at
`PostSptModLoader`, after all OnLoads) exists to discard. See LeavesUtil handoff §3.1–3.2.

## 5. Faithful-port note

`GrenadeLauncherErgo` had a genuine TS bug (hardcoded value ignoring config) — fixed here, and
flagged in the code comment. Everything else is a faithful behavioral port.

## 6. Files

| File | Role |
|---|---|
| `src/TreasureBox.cs` | All tweaks (§3). |
| `src/Config.cs` | One bool per tweak + their parameters (values, id lists). |
| `config/config.jsonc` | The toggles. |

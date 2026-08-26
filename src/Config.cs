using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves_TreasureBox;

// Mirrors config/config.jsonc. Several fields interfaces.ts declared (PistolScopes, keyDurability,
// namelister, botRangeAdjustment, levelRanges, scavgingynerf, flareSpecial, shturmanKeyBuff,
// disableCoopReward, disableFenceRepReward, IAmAScumAndWantToRemoveSupporterNames, namesToAdd) were
// never referenced by mod.ts's actual logic and aren't in the shipped config either - dropped
// rather than ported as dead config. The "debug" streetsloot dump (hardcoded to a personal,
// long-gone SPT install path) was dropped entirely per instruction.
public record TreasureBoxConfig
{
    [JsonPropertyName("GLUCK")]
    public required bool GLUCK { get; set; }

    [JsonPropertyName("M14")]
    public required bool M14 { get; set; }

    [JsonPropertyName("GrenadeLauncherErgo")]
    public required bool GrenadeLauncherErgo { get; set; }

    [JsonPropertyName("GrenadeLauncherErgoValue")]
    public required double GrenadeLauncherErgoValue { get; set; }

    [JsonPropertyName("UMPDrum")]
    public required bool UMPDrum { get; set; }

    [JsonPropertyName("UMPDrumVector")]
    public required bool UMPDrumVector { get; set; }

    [JsonPropertyName("UMPDrumAddToTraders")]
    public required bool UMPDrumAddToTraders { get; set; }

    [JsonPropertyName("zeroToHeroPouch")]
    public required bool ZeroToHeroPouch { get; set; }

    [JsonPropertyName("ragfairLevelOverride")]
    public required bool RagfairLevelOverride { get; set; }

    [JsonPropertyName("ragFairMinUserLevel")]
    public required int RagFairMinUserLevel { get; set; }

    [JsonPropertyName("AK12Muzzles")]
    public required bool AK12Muzzles { get; set; }

    [JsonPropertyName("fuckfakevodka")]
    public required bool FuckFakeVodka { get; set; }

    [JsonPropertyName("rpdmods")]
    public required bool RpdMods { get; set; }

    [JsonPropertyName("enableFenceBlacklist")]
    public required bool EnableFenceBlacklist { get; set; }

    [JsonPropertyName("fenceBlacklist")]
    public required List<MongoId> FenceBlacklist { get; set; }

    [JsonPropertyName("memeUSP45")]
    public required bool MemeUSP45 { get; set; }

    [JsonPropertyName("enableTraderRefreshAdjustment")]
    public required bool EnableTraderRefreshAdjustment { get; set; }

    [JsonPropertyName("traderRefreshTimeAdjustmentMin")]
    public required int TraderRefreshTimeAdjustmentMin { get; set; }

    [JsonPropertyName("traderRefreshTimeAdjustmentMax")]
    public required int TraderRefreshTimeAdjustmentMax { get; set; }

    [JsonPropertyName("traderRefreshTimeAdjustmentLimit")]
    public required int TraderRefreshTimeAdjustmentLimit { get; set; }

    [JsonPropertyName("enableSuppressorErgoAdjustment")]
    public required bool EnableSuppressorErgoAdjustment { get; set; }

    [JsonPropertyName("suppressorErgoAdjustment")]
    public required double SuppressorErgoAdjustment { get; set; }

    [JsonPropertyName("m700Barrel")]
    public required bool M700Barrel { get; set; }

    [JsonPropertyName("enableQuests")]
    public required bool EnableQuests { get; set; }

    [JsonPropertyName("quests")]
    public required List<MongoId> Quests { get; set; }

    [JsonPropertyName("junkbox")]
    public required bool Junkbox { get; set; }

    [JsonPropertyName("BTRChanges")]
    public required bool BTRChanges { get; set; }

    [JsonPropertyName("keychanges")]
    public required bool KeyChanges { get; set; }

    [JsonPropertyName("bitcoinPrice")]
    public required bool BitcoinPrice { get; set; }

    // The divisor the handbook Bitcoin price is cut by when bitcoinPrice is on (e.g. 4 = a quarter of
    // vanilla). Non-required so older configs load; defaults to 4. A value <= 0 is treated as 1
    // (no change) rather than dividing by zero.
    [JsonPropertyName("bitcoinPriceDivisor")]
    public double BitcoinPriceDivisor { get; set; } = 4;

    // Which currency a trader PAYS YOU IN when you sell to them - the trader's own base "currency"
    // field (vanilla Peacekeeper uses USD; that's the same mechanism). Nothing to do with what they
    // sell you, which BarterEconomy handles. traderId -> "RUB" | "USD" | "EUR" | "GP".
    // Both non-`required` with defaults, so configs written before these keys existed still load.
    // Raise mod-added ammo that its author priced below what the game charges for equally-lethal
    // rounds, using vanilla's own penetration->price curve as the yardstick. Floor only - never
    // cheapens ammo, never touches vanilla. See TreasureBox.FixModAmmoPricing.
    [JsonPropertyName("fixModAmmoPricing")]
    public bool FixModAmmoPricing { get; set; }

    [JsonPropertyName("changeTraderCurrency")]
    public bool ChangeTraderCurrency { get; set; }

    [JsonPropertyName("traderCurrencies")]
    public Dictionary<string, string> TraderCurrencies { get; set; } = new();

    // Adds the missing workbench craft for the "Nut can" (a WTT-ContentBackport item): flat
    // screwdriver (tool) + a can of Majaica coffee beans, 1 hour. Skipped if the item isn't in the
    // database (WTT not installed). Non-required so older configs load (defaulting on).
    [JsonPropertyName("nutCanCraft")]
    public bool NutCanCraft { get; set; } = true;

    // Adds the missing hideout crafts for two other WTT-ContentBackport containers whose live-Tarkov
    // recipes didn't come across: the Toolbox (Discord Version) in the Lavatory, and the Poster tube
    // at the Workbench. Each is skipped individually if its item isn't in the database (WTT not
    // installed). Non-required so older configs load (defaulting on).
    [JsonPropertyName("backportContainerCrafts")]
    public bool BackportContainerCrafts { get; set; } = true;

    // Gives the "Thumb drive with military data" quest item (6889e4cd3ff7e9cfbc0c507c) a RED inventory
    // background so it can't be mistaken for the ordinary flash drive it otherwise looks identical to -
    // it spawns commonly as loot. Skipped if the item isn't in the database. Non-required (defaults on).
    [JsonPropertyName("redMilitaryFlashDrive")]
    public bool RedMilitaryFlashDrive { get; set; } = true;

    // Whitelists the DrakiaXYZ-GildedKeyStorage items (golden keychains + keycard holder) into the
    // vanilla Key Box's grid, so those custom containers can be stored inside it. They aren't parented
    // to the Keys category the Key Box already allows, so each has to be listed explicitly. Every id in
    // gildedKeysInKeyBox that isn't in the database (GildedKeyStorage not installed, or an id changed)
    // is skipped individually. Non-required so older configs load (defaulting on).
    [JsonPropertyName("gildedKeysInKeyBox")]
    public bool GildedKeysInKeyBox { get; set; } = true;

    // The item ids whitelisted into the Key Box when gildedKeysInKeyBox is on. Defaults to the three
    // Golden Keychains and the Golden Keycard Holder. The Golden Key Box itself is deliberately left
    // out (nesting one key container inside another), but can be added here if wanted.
    [JsonPropertyName("gildedKeysInKeyBoxItems")]
    public List<MongoId> GildedKeysInKeyBoxItems { get; set; } = new()
    {
        "661cb36922c9e10dc2d9514b", // Golden Key box
        "661cb372e5eb56290da76c3e", // Golden Keychain Mk. I
        "661cb3743bf00d3d145518b3", // Golden Keychain Mk. II
        "661cb376b16226f648eb0cdc", // Golden Keychain Mk. III
        "661cb36f5441dc730e28bcb0"  // Golden Keycard Holder
    };

    // Restricts who may talk to the "Commando" in-game chatbot (the bot behind `spt give`, plus the
    // `spt trader` / `spt profile` cheat commands). When on, only the profile ids in CommandoWhitelist
    // get a response; everyone else is told they aren't authorized. Off = vanilla (anyone can use it).
    // Unlike the other tweaks this isn't an OnLoad database edit - it's a DI override of the live
    // CommandoDialogChatBot service, wired up in CommandoWhitelist.cs. Non-required so older configs
    // load (defaulting off = vanilla behavior). See CommandoWhitelist.cs.
    [JsonPropertyName("commandoWhitelistEnabled")]
    public bool CommandoWhitelistEnabled { get; set; }

    // The profile ids (info.id / sessionId, NOT the AID) allowed to use the Commando bot when the
    // whitelist is on. Populated with every profile currently on this install; prune it to restrict.
    [JsonPropertyName("commandoWhitelist")]
    public List<MongoId> CommandoWhitelist { get; set; } = new();

    // Lowers the two absurd finish-condition counts on the four vanilla Drip-Out quests (kill 100
    // Scavs + hand over 50 cosmetics, per faction variant of Part 1 and Part 2) down to the counts
    // below. Only the counts change - conditions, targets and rewards are left alone. It's a plain
    // database edit, so it reaches every profile; Questrandomizer randomizes these quests but
    // re-extracts from the live database on a forced re-roll, so an already-rolled profile just needs
    // a Drip-Out re-roll from the QR web UI to pick the new counts up. Non-required so older configs
    // load. See TreasureBox.NerfDripOut.
    [JsonPropertyName("nerfDripOut")]
    public bool NerfDripOut { get; set; } = true;

    // Kill count the Drip-Out CounterCreator (kill Scavs) objective is lowered to when nerfDripOut is on.
    [JsonPropertyName("dripOutKillCount")]
    public int DripOutKillCount { get; set; } = 1;

    // Item count the Drip-Out HandoverItem (hand over cosmetics) objective is lowered to when nerfDripOut is on.
    [JsonPropertyName("dripOutHandoverCount")]
    public int DripOutHandoverCount { get; set; } = 1;

    // Removes the reload-speed PENALTY from every magazine that has one: a magazine's
    // LoadUnloadModifier is a percent modifier on how fast rounds load/unload, and a positive value
    // slows it down (drum mags, some extended mags). When on, any positive modifier is lowered to 0
    // (penalty gone) while negative modifiers - the mags that load FASTER than baseline - are left
    // untouched, so you only ever gain. Plain database edit, reaches every profile. Non-required so
    // older configs load (defaulting off = vanilla). See TreasureBox.MagazineLoadSpeed.
    [JsonPropertyName("removeMagazineLoadPenalty")]
    public bool RemoveMagazineLoadPenalty { get; set; }

    // Overrides the base-game airdrop TYPE weightings at runtime, so the base file
    // (SPT_Data/configs/airdrop.json) stays untouched - base files are off-limits (checksummed) and
    // editing them is considered wrong. When on, the weights in airdropWeights are written over the
    // live AirdropConfig in memory. Non-required so older configs load (defaulting off). See
    // TreasureBox.OverrideAirdropWeights.
    [JsonPropertyName("airdropWeightOverride")]
    public bool AirdropWeightOverride { get; set; }

    // Relative type weights, keyed by SPT airdrop type name (mixed / weaponArmor / foodMedical /
    // barter / radar / toiletPaper). These are RELATIVE weights, not percentages: the server rolls a
    // type weighted by their sum. Only the types listed here are changed; any type left out keeps its
    // vanilla weight. Vanilla is mixed 500 / weaponArmor 400 / foodMedical 100 / barter 100 (radar 0,
    // toiletPaper 1), which makes foodMedical ~9% of drops. Unknown key names are skipped with a warning.
    [JsonPropertyName("airdropWeights")]
    public Dictionary<string, double> AirdropWeights { get; set; } = new();
}

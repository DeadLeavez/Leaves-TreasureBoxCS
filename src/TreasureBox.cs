using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.Mod;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

namespace Leaves_TreasureBox;

// Grab-bag of small, independently-toggled tweaks. No periodic behavior (no IOnUpdate).
//
// Singleton IS required, despite having only one lifecycle interface: VanillaAmmoSnapshot and
// ModAmmoPricingSetup inject this class as a plain constructor parameter while the IOnLoad
// collection resolves it separately. Without Singleton those are two different instances, so the
// snapshot would be captured on one and read on another (which has no config loaded) - the same
// dual-resolution pitfall documented on LeavesUtils.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public class TreasureBox(
    DatabaseService databaseService,
    ModHelper modHelper,
    HandbookHelper handbookHelper,
    CustomItemService customItemService,
    ICloner cloner,
#pragma warning disable CS0618 // ConfigServer is obsolete (see BarterEconomy.ApplyDebugFastResupply's comment) but still the only way to reach TraderConfig/QuestConfig/InventoryConfig in 4.0.13.
    ConfigServer configServer)
#pragma warning restore CS0618
    : IOnLoad
{
    private TreasureBoxConfig config = null!;
    private Dictionary<MongoId, TemplateItem> itemDB = null!;

    private static readonly MongoId AmmoClass = "5485a8684bdc2da71d8b4567";

    // Every item id that shipped with the game - captured by VanillaAmmoSnapshot before any mod
    // loads. Used by FixModAmmoPricing to tell vanilla ammo (the pricing REFERENCE) from mod ammo
    // (the thing being corrected).
    private HashSet<MongoId> vanillaItemIds = new();

    public void CaptureVanillaItemIds(IEnumerable<MongoId> ids)
    {
        vanillaItemIds = new HashSet<MongoId>(ids);
    }

    // Raises the handbook price of mod-added ammo that its author priced below what the GAME itself
    // charges for equally-lethal rounds.
    //
    // Why this is needed at all: mod authors have no reference point for a caliber vanilla doesn't
    // have. Measured on WTT: where a vanilla peer exists their ammo is priced FINE (7.62x25 mod
    // rounds run 150-560 vs vanilla's 48-70), but the unique calibers are guesses - .44 Magnum Red
    // Tracer shipped at 54 roubles, cheaper than vanilla's cheapest pistol round. With valueCutoff
    // at 0 that made a 40-round box worth ~3k, i.e. a tier-1 barter: a bottle of water for 40 rounds
    // of magnum.
    //
    // The rule: value ammo by PENETRATION, using vanilla's own price curve as the yardstick. Vanilla
    // prices track penetration cleanly (median roughly doubles per 10 pen: 73 -> 145 -> 317 -> 720
    // -> 1230 -> 3280), so the median price of vanilla rounds in the same 10-pen band is a fair,
    // game-authored answer to "what should this be worth".
    //
    // It is a FLOOR, never a cap: ammo an author priced generously keeps its price. Vanilla ammo is
    // never touched - it IS the reference, and flooring it would just inflate every below-median
    // round. Runs before HandbookCallbacks caches prices - see ModAmmoPricingSetup for that trap.
    public void FixModAmmoPricing()
    {
        if (!config.FixModAmmoPricing)
        {
            return;
        }
        if (vanillaItemIds.Count == 0)
        {
            Leaves.Utils.Instance.printColor("[TreasureBox] Mod ammo pricing: vanilla snapshot missing - skipping (would have no reference to price against).", LogTextColor.Red);
            return;
        }

        var items = databaseService.GetItems();
        var handbook = databaseService.GetHandbook().Items;
        var handbookByItem = new Dictionary<MongoId, HandbookItem>();
        foreach (var entry in handbook)
        {
            handbookByItem.TryAdd(entry.Id, entry);
        }

        bool IsAmmo(MongoId id)
        {
            var seen = new HashSet<MongoId>();
            var current = id;
            while (items.TryGetValue(current, out var item) && seen.Add(current))
            {
                if (item.Parent == AmmoClass)
                {
                    return true;
                }
                current = item.Parent;
            }
            return false;
        }

        // Build the reference curve: pen band (rounded to 10) -> median vanilla price.
        var bands = new Dictionary<int, List<double>>();
        foreach (var (id, item) in items)
        {
            if (item.Type != "Item" || !vanillaItemIds.Contains(id) || !IsAmmo(id))
            {
                continue;
            }
            if (item.Properties?.PenetrationPower is not { } pen || !handbookByItem.TryGetValue(id, out var hb) || hb.Price is not { } price || price <= 0)
            {
                continue;
            }
            int band = (int)(pen / 10) * 10;
            if (!bands.TryGetValue(band, out var list))
            {
                list = new List<double>();
                bands[band] = list;
            }
            list.Add(price);
        }

        var medians = new Dictionary<int, double>();
        foreach (var (band, prices) in bands)
        {
            // Bands with almost no vanilla rounds aren't a trustworthy yardstick (the pen-100+ band
            // is a single oddity priced at 100), so ignore them rather than invent a floor from one
            // sample.
            if (prices.Count < 3)
            {
                continue;
            }
            prices.Sort();
            medians[band] = prices[prices.Count / 2];
        }

        int raised = 0;
        foreach (var (id, item) in items)
        {
            if (item.Type != "Item" || vanillaItemIds.Contains(id) || !IsAmmo(id))
            {
                continue; // vanilla ammo is the reference - never touched
            }
            if (item.Properties?.PenetrationPower is not { } pen || !handbookByItem.TryGetValue(id, out var hb))
            {
                continue;
            }
            if (!medians.TryGetValue((int)(pen / 10) * 10, out var floor))
            {
                continue; // no trustworthy vanilla band for this penetration
            }

            double current = hb.Price ?? 0;
            if (current >= floor)
            {
                continue; // author priced it at or above the game's own going rate - leave it alone
            }

            hb.Price = Math.Ceiling(floor);
            raised++;
            Leaves.Utils.Instance.printColor(
                $"[TreasureBox] \t{Leaves.Utils.Instance.getLocale("en", id.ToString(), " Name") ?? item.Name}: {current} -> {hb.Price} (pen {pen}, vanilla median for that band).",
                LogTextColor.Yellow);
        }

        Leaves.Utils.Instance.printColor(
            $"[TreasureBox] Mod ammo pricing: raised {raised} under-priced mod round(s) to vanilla's going rate for their penetration.",
            LogTextColor.Cyan);
    }

    // The four vanilla Drip-Out quests - USEC + BEAR variants of Part 1 and Part 2. Each one's finish
    // conditions are a perfectly sane SHAPE (kill Scavs via a CounterCreator, hand over cosmetics via
    // a HandoverItem), just with insane counts - kill 100 and hand over 50 - for a purely cosmetic
    // reward.
    private static readonly MongoId[] DripOutQuestIds =
    {
        "6613f3007f6666d56807c929", // Drip-Out - Part 1 (variant A)
        "66151401efb0539ae10875ae", // Drip-Out - Part 1 (variant B)
        "6613f307fca4f2f386029409", // Drip-Out - Part 2 (variant A)
        "6615141bfda04449120269a7"  // Drip-Out - Part 2 (variant B)
    };

    // Lowers ONLY the two counts on each Drip-Out quest's finish conditions - conditions, targets and
    // rewards are left exactly as vanilla ships them, so nothing structural changes and no per-profile
    // bookkeeping is needed. This is a database edit, so it reaches every profile at once.
    //
    // Questrandomizer randomizes these quests, but it re-extracts its outline from the LIVE quest
    // database when a quest is force-rolled, and reads the finish count from whatever it finds there.
    // We edit at OnLoad, long before any request arrives, so a fresh roll sees the lowered counts. A
    // profile that already rolled Drip-Out keeps its stored (insane) outline until the player forces a
    // re-roll of the pair from the QR web UI - at which point QR reads the count we left behind here.
    private void NerfDripOut()
    {
        var quests = databaseService.GetQuests();
        int edited = 0;
        foreach (MongoId questId in DripOutQuestIds)
        {
            if (!quests.TryGetValue(questId, out var quest))
            {
                continue; // the other faction's variant, or the quest isn't in this install
            }
            var finishConditions = quest.Conditions?.AvailableForFinish;
            if (finishConditions == null)
            {
                continue;
            }
            foreach (var condition in finishConditions)
            {
                if (condition.ConditionType == "CounterCreator")
                {
                    condition.Value = config.DripOutKillCount;
                    edited++;
                }
                else if (condition.ConditionType == "HandoverItem")
                {
                    condition.Value = config.DripOutHandoverCount;
                    edited++;
                }
            }
        }

        Leaves.Utils.Instance.printColor(
            $"[TreasureBox] Drip-Out nerf: lowered {edited} condition count(s) (kill {config.DripOutKillCount}, handover {config.DripOutHandoverCount}). Force a Drip-Out re-roll in the Questrandomizer UI to apply it to profiles that already rolled it.",
            LogTextColor.Green);
    }

    public Task OnLoad()
    {
        string modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        config = modHelper.GetJsonDataFromFile<TreasureBoxConfig>(modPath, "config/config.jsonc");
        itemDB = databaseService.GetItems();

        Leaves.Utils.Instance.printColor("[TreasureBox] TreasureBox Starting");

        if (config.EnableFenceBlacklist)
        {
            FenceBlacklist();
        }
        if (config.GLUCK)
        {
            Gluck();
        }
        if (config.M14)
        {
            M14();
        }
        if (config.GrenadeLauncherErgo)
        {
            GrenadeLauncherErgo();
        }
        if (config.UMPDrum)
        {
            UmpDrum();
        }
        if (config.ZeroToHeroPouch)
        {
            ZeroToHeroPouch();
        }
        if (config.RagfairLevelOverride)
        {
            RagfairMinLevelOverride();
        }
        if (config.AK12Muzzles)
        {
            Ak12Muzzle();
        }
        if (config.FuckFakeVodka)
        {
            FakeVodkaFix();
        }
        if (config.RedMilitaryFlashDrive)
        {
            MilitaryFlashDriveFix();
        }
        if (config.NerfDripOut)
        {
            NerfDripOut();
        }
        if (config.RpdMods)
        {
            RpdMods();
        }
        if (config.MemeUSP45)
        {
            MemeUsp45();
        }
        if (config.EnableTraderRefreshAdjustment)
        {
            TraderRefresh();
        }
        if (config.EnableSuppressorErgoAdjustment)
        {
            SuppressorErgoAdjustment();
        }
        if (config.M700Barrel)
        {
            M700Barrel();
        }
        if (config.EnableQuests)
        {
            EnableQuests();
        }
        if (config.Junkbox)
        {
            Junkbox();
        }
        if (config.BTRChanges)
        {
            BtrChanges();
        }
        if (config.KeyChanges)
        {
            Leaves.Utils.Instance.printColor("[TreasureBox] Making some keys have charges.", LogTextColor.Cyan);
            ChangeKeys();
        }
        if (config.BitcoinPrice)
        {
            BitcoinPrice();
        }
        if (config.GildedKeysInKeyBox)
        {
            GildedKeysInKeyBox();
        }
        if (config.ChangeTraderCurrency)
        {
            ChangeTraderCurrency();
        }
        if (config.AirdropWeightOverride)
        {
            OverrideAirdropWeights();
        }
        // These crafts all output WTT-ContentBackport items. We can't check the item db here: WTT
        // injects its custom items AFTER this OnLoad (PostDBModLoader+1), so GetItems() wouldn't have
        // them yet. Instead gate on WTT-ContentBackport being installed at all - if it is, its item
        // ids are valid and will be in the db by the time the client reads hideout recipes (a recipe's
        // EndProduct is just an id string; nothing validates it at add-time).
        if (config.NutCanCraft || config.BackportContainerCrafts)
        {
            if (WttContentBackportInstalled())
            {
                if (config.NutCanCraft)
                {
                    AddNutCanRecipe();
                }
                if (config.BackportContainerCrafts)
                {
                    AddContainerCrafts();
                }
            }
            else
            {
                Leaves.Utils.Instance.printColor("[TreasureBox] WTT-ContentBackport not installed - skipping its item crafts (nut can / toolbox / poster tube).", LogTextColor.Red);
            }
        }

        return Task.CompletedTask;
    }

    // Overrides the base-game airdrop TYPE weightings at runtime. The vanilla weights live in
    // SPT_Data/configs/airdrop.json, which we deliberately DON'T edit - base files are off-limits
    // (checksummed) and editing them is considered wrong. Instead we reach the already-loaded
    // AirdropConfig through the config server and rewrite the weights in the live in-memory instance;
    // the server rolls the airdrop type fresh from that instance each raid, so the override sticks.
    //
    // Only the types named in config.AirdropWeights are touched - any type absent keeps its vanilla
    // weight, so this is a targeted bump, not a full replacement. The weights are RELATIVE, not
    // percentages: the server picks a type by weighted roll over their sum. Vanilla is mixed 500 /
    // weaponArmor 400 / foodMedical 100 / barter 100 (radar 0, toiletPaper 1) => foodMedical ~9% of
    // drops; raising foodMedical + barter to 300 each (sum 1501) makes each ~20%.
    private void OverrideAirdropWeights()
    {
        var airdropConfig = configServer.GetConfig<AirdropConfig>();
        var weightings = airdropConfig.AirdropTypeWeightings;

        foreach (var (typeName, weight) in config.AirdropWeights)
        {
            if (!Enum.TryParse<SptAirdropTypeEnum>(typeName, out var type))
            {
                Leaves.Utils.Instance.printColor($"[TreasureBox] Airdrop weight override: '{typeName}' isn't a known airdrop type - skipping it.", LogTextColor.Red);
                continue;
            }
            weightings[type] = weight;
            Leaves.Utils.Instance.printColor($"[TreasureBox] Airdrop weight override: {typeName} = {weight}.", LogTextColor.Cyan);
        }
    }

    // True when the WTT-ContentBackport mod is installed alongside us (sibling folder in user/mods).
    // We check the folder rather than the item db because our OnLoad runs before WTT injects its
    // items - see the call site in OnLoad. If WTT is present, its item ids (nut can, toolbox, poster
    // tube) are valid and will be in the db by the time anything reads them.
    private bool WttContentBackportInstalled()
    {
        var myPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var modsRoot = System.IO.Directory.GetParent(myPath)?.FullName;
        return modsRoot != null && System.IO.Directory.Exists(System.IO.Path.Combine(modsRoot, "WTT-ContentBackport"));
    }

    // Live Tarkov has a workbench craft for the "Nut can", but the WTT-ContentBackport version of
    // the item ships without the recipe. Re-add it: flat screwdriver (Tool - stays), a can of
    // Majaica coffee beans (consumed), one hour, at the workbench. Structure copied from a real
    // vanilla screwdriver craft (production.json 5de95f1c...). Only called when WTT-ContentBackport
    // is installed (checked at the call site), so the output item is guaranteed to exist by run time.
    private void AddNutCanRecipe()
    {
        MongoId nutCan = "693bfb50d5c25889e701d444";        // WTT-ContentBackport "Nut can"
        MongoId flatScrewdriver = "5d63d33b86f7746ea9275524";
        MongoId majaicaCoffee = "5af0484c86f7740f02001f7f";

        Leaves.Utils.Instance.addCraft(
            // Fixed, distinctive id - a craft id must stay stable across restarts so an in-progress
            // craft keeps resolving to its recipe. Not a real BSG id, so it can't collide.
            "1eafca11beefca11beefca11",
            HideoutAreas.Workbench,
            new List<Requirement>
            {
                Leaves.Utils.Instance.craftAreaRequirement((int)HideoutAreas.Workbench, 1),
                Leaves.Utils.Instance.craftToolRequirement(flatScrewdriver),
                Leaves.Utils.Instance.craftItemRequirement(majaicaCoffee, 1)
            },
            3600,
            nutCan);

        Leaves.Utils.Instance.printColor("[TreasureBox] Added the Nut can workbench craft (flat screwdriver + Majaica coffee, 1h).", LogTextColor.Cyan);
    }

    // Two more WTT-ContentBackport containers that ship without their live-Tarkov crafts. Re-add them.
    // Toolbox (Discord Version): 6 kinds of hardware + a Lega Medal, crafted in the Lavatory ("toilet"
    // crafting spot, where BSG actually put it), 3h. Poster tube: WD-40 + a few flea trinkets at the
    // Workbench, 1h. Only called when WTT-ContentBackport is installed (checked at the call site), so
    // both output items are guaranteed present by run time. Craft ids are fixed, distinctive, and not
    // real BSG ids (so they can't collide) - must stay stable so an in-progress craft still resolves
    // to its recipe across restarts. Item counts/tpls per the recipes we settled on.
    private void AddContainerCrafts()
    {
        MongoId toolbox = "688b2dba67bf6fa26c07e918";      // WTT-ContentBackport "Toolbox (Discord Version)"
        MongoId posterTube = "697883474afb9f7de10380ba";   // WTT-ContentBackport "Poster tube"

        Leaves.Utils.Instance.addCraft(
            "1eafb0a71eafb0a71eafb0a7",
            // Area 2 = the Lavatory ("toilet" crafting spot - vanilla crafts Ox bleach, KEKTAPE,
            // fuel tanks and armor here). SPT's HideoutAreas enum has no friendly member for it,
            // so use the raw EFT area number the game and production.json already use.
            (HideoutAreas)2,
            new List<Requirement>
            {
                Leaves.Utils.Instance.craftAreaRequirement(2, 1),
                Leaves.Utils.Instance.craftItemRequirement("57347c5b245977448d35f6e1", 4), // Bolts
                Leaves.Utils.Instance.craftItemRequirement("57347c77245977448d35f6e2", 4), // Screw nuts
                Leaves.Utils.Instance.craftItemRequirement("5d1b36a186f7742523398433", 2), // Metal fuel tank
                Leaves.Utils.Instance.craftItemRequirement("61bf7b6302b3924be92fa8c3", 4), // Metal spare parts
                Leaves.Utils.Instance.craftItemRequirement("5e2af29386f7746d4159f077", 2), // KEKTAPE duct tape
                Leaves.Utils.Instance.craftItemRequirement("6656560053eaaa7a23349c86", 1)  // Lega Medal
            },
            10800,
            toolbox);
        Leaves.Utils.Instance.printColor("[TreasureBox] Added the Toolbox (Discord Version) Lavatory craft (hardware + Lega Medal, 3h).", LogTextColor.Cyan);

        Leaves.Utils.Instance.addCraft(
            "1eaf70be1eaf70be1eaf70be",
            HideoutAreas.Workbench,
            new List<Requirement>
            {
                Leaves.Utils.Instance.craftAreaRequirement((int)HideoutAreas.Workbench, 1),
                Leaves.Utils.Instance.craftItemRequirement("590c5c9f86f77477c91c36e7", 1), // WD-40 (400ml)
                Leaves.Utils.Instance.craftItemRequirement("5734758f24597738025ee253", 1), // Golden neck chain
                Leaves.Utils.Instance.craftItemRequirement("62a09d3bcf4a99369e262447", 1), // Gingy keychain
                Leaves.Utils.Instance.craftItemRequirement("60b0f6c058e0b0481a09ad11", 1)  // WZ Wallet
            },
            3600,
            posterTube);
        Leaves.Utils.Instance.printColor("[TreasureBox] Added the Poster tube Workbench craft (WD-40 + trinkets, 1h).", LogTextColor.Cyan);
    }

    // Sets which currency a trader PAYS YOU IN when you sell to them. It's a single field on the
    // trader's own base data - vanilla Peacekeeper already ships with USD, so this is just using
    // the game's existing mechanism rather than inventing one. Untouched by BarterEconomy and
    // LimitedTraders: both only ever restore/rewrite Assort, never Base.
    private void ChangeTraderCurrency()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] Trader currency changes", LogTextColor.Cyan);
        var traders = databaseService.GetTraders();

        foreach (var (traderIdRaw, currencyName) in config.TraderCurrencies)
        {
            var traderId = new MongoId(traderIdRaw);
            if (!traders.TryGetValue(traderId, out var trader))
            {
                Leaves.Utils.Instance.printColor($"[TreasureBox] \tTrader {traderIdRaw} isn't in the database - skipped.", LogTextColor.Red);
                continue;
            }
            if (!Enum.TryParse<CurrencyType>(currencyName, true, out var currency))
            {
                Leaves.Utils.Instance.printColor($"[TreasureBox] \t'{currencyName}' isn't a valid currency (RUB/USD/EUR/GP) - skipped.", LogTextColor.Red);
                continue;
            }

            trader.Base.Currency = currency;

            // A trader's loyalty-level spend thresholds (MinSalesSum) are read in that trader's own
            // currency. The vanilla numbers assume roubles, so switching a trader to EUR/USD - each
            // worth ~130 roubles - silently turns an "800,000 spend" requirement into 800,000 EUR
            // (~106M roubles). Re-express each threshold in the new currency using its handbook rate
            // so the rouble-equivalent stays the same, rounded to the nearest 1000. Roubles (rate 1)
            // and unknown rates are left alone.
            double roublesPerUnit = handbookHelper.GetTemplatePrice(currency.GetCurrencyTpl());
            if (roublesPerUnit > 1 && trader.Base.LoyaltyLevels != null)
            {
                foreach (var level in trader.Base.LoyaltyLevels)
                {
                    if (level.MinSalesSum is { } sum && sum > 0)
                    {
                        level.MinSalesSum = (long)(Math.Round(sum / roublesPerUnit / 1000.0) * 1000.0);
                    }
                }
            }

            Leaves.Utils.Instance.printColor($"[TreasureBox] \t{Leaves.Utils.Instance.getTraderNickname(traderId)} now pays in {currency} (loyalty spend thresholds converted at {roublesPerUnit}₽ each).", LogTextColor.Yellow);
        }
    }

    private void Gluck()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] GLUCK enabled.", LogTextColor.Cyan);
        var glock17Slots = itemDB["5a7ae0c351dfba0017554310"].Properties!.Slots!.ToList();
        var glock18Slots = itemDB["5b1fa9b25acfc40018633c01"].Properties!.Slots!.ToList();

        for (int i = 0; i < glock18Slots.Count; i++)
        {
            var sourceFilter = glock17Slots[i].Properties!.Filters!.First().Filter!;
            var targetFilter = glock18Slots[i].Properties!.Filters!.First().Filter!;
            foreach (var filterId in sourceFilter)
            {
                targetFilter.Add(filterId);
            }
        }
    }

    private void M14()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] M14 enabled.", LogTextColor.Cyan);
        MongoId m1aId = "5aafa857e5b5b00018480968";
        itemDB[m1aId].Properties!.BFirerate = 725;
        itemDB[m1aId].Properties!.WeapFireType = new HashSet<string> { "single", "fullauto" };
    }

    private void GrenadeLauncherErgo()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] GrenadeLauncherErgo enabled.", LogTextColor.Cyan);
        MongoId launcherParent = "55818b014bdc2ddc698b456b";
        foreach (var item in itemDB.Values)
        {
            if (item.Parent == launcherParent)
            {
                // Original TS hardcoded -10 here instead of using config.GrenadeLauncherErgoValue
                // (which the very next line's log message references) - a genuine bug, fixed here
                // to actually use the configured value like the log always claimed it did. Zero
                // effect under the shipped default (also -10), but would matter the moment someone
                // changed the config.
                item.Properties!.Ergonomics = config.GrenadeLauncherErgoValue;
                Leaves.Utils.Instance.printColor($"[TreasureBox] \tGrenadeLauncherErgo - Edited. {item.Name} to {config.GrenadeLauncherErgoValue}", LogTextColor.Yellow);
            }
        }
    }

    private void UmpDrum()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] UMPDrum enabled.", LogTextColor.Cyan);

        MongoId peacekeeperID = "5935c25fb3acc3127c3d8cd9";
        MongoId mechanicID = "5a7c2eca46aef81a7ca2145d";
        MongoId m4drumID = "59c1383d86f774290a37e0ca";
        string newLocaleName = ".45 ACP Magpul PMAG D-60 60-round magazine";
        double price = 25000;
        MongoId newID = "6773b8832c3a1a1c5e32af68";

        var newItemDetails = new NewItemFromCloneDetails
        {
            ItemTplToClone = m4drumID,
            OverrideProperties = new TemplateItemProperties
            {
                Cartridges = new List<Slot>
                {
                    new Slot
                    {
                        Id = new MongoId().ToString(),
                        MaxCount = 50,
                        Name = "cartridges",
                        Parent = newID.ToString(),
                        Properties = new SlotProperties
                        {
                            Filters = new List<SlotFilter>
                            {
                                new SlotFilter
                                {
                                    Filter = new HashSet<MongoId>
                                    {
                                        "5e81f423763d9f754677bf2e",
                                        "5efb0cabfb3e451d70735af5",
                                        "5efb0fc6aeb21837e749c801",
                                        "5efb0d4f4bc50b58e81710f3",
                                        "5ea2a8e200685063ec28c05a"
                                    }
                                }
                            }
                        },
                        Prototype = "5748538b2459770af276a261"
                    }
                }
            },
            NewId = newID.ToString(),
            ParentId = "5448bc234bdc2d3c308b4569",
            HandbookParentId = "5b5f754a86f774094242f19b",
            FleaPriceRoubles = price,
            HandbookPriceRoubles = price,
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new LocaleDetails
                {
                    Name = newLocaleName,
                    ShortName = newLocaleName,
                    Description = "Drum mag for the UMP45 HELL YISS BRUTHA."
                }
            }
        };

        customItemService.CreateItemFromClone(newItemDetails);

        //Add the mag to the ump
        Leaves.Utils.Instance.getSlotFilter(itemDB["5fc3e272f8b6a877a729eac5"], 0).Add(newID);

        if (config.UMPDrumVector)
        {
            Leaves.Utils.Instance.printColor("[TreasureBox] \tUMPDrum enabled for vector.", LogTextColor.Yellow);
            //Add the mag to the vector
            Leaves.Utils.Instance.getSlotFilter(itemDB["5fb64bc92b1b027b1f50bcf2"], 0).Add(newID);
        }

        if (config.UMPDrumAddToTraders)
        {
            Leaves.Utils.Instance.printColor("[TreasureBox] \tUMPDrum added to traders.", LogTextColor.Yellow);

            // Create barter scheme object
            var barterSchemeToAdd = new BarterScheme
            {
                Count = handbookHelper.GetTemplatePrice(newID),
                Template = Money.ROUBLES
            };

            // Create item object
            var itemToAdd = new Item
            {
                Id = newID,
                Template = newID,
                ParentId = "hideout",
                SlotId = "hideout",
                Upd = new Upd
                {
                    StackObjectsCount = 9999999,
                    UnlimitedCount = true
                }
            };

            var traders = databaseService.GetTraders();
            var pkAssort = traders[peacekeeperID].Assort;
            var meAssort = traders[mechanicID].Assort;

            // add item to assort
            pkAssort.Items.Add(itemToAdd);
            pkAssort.BarterScheme[newID] = new List<List<BarterScheme>> { new List<BarterScheme> { barterSchemeToAdd } };
            pkAssort.LoyalLevelItems[newID] = 3;

            meAssort.Items.Add(itemToAdd);
            meAssort.BarterScheme[newID] = new List<List<BarterScheme>> { new List<BarterScheme> { barterSchemeToAdd } };
            meAssort.LoyalLevelItems[newID] = 3;
        }
    }

    private void ZeroToHeroPouch()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] Zero to Hero pouch Enabled", LogTextColor.Cyan);
        var zth = databaseService.GetProfileTemplates()["SPT Zero to hero"];
        ReplaceAlphaWithPouch(zth.Bear);
        ReplaceAlphaWithPouch(zth.Usec);
    }

    private void ReplaceAlphaWithPouch(TemplateSide? side)
    {
        foreach (var item in side!.Character!.Inventory!.Items!)
        {
            if (item.Template == "544a11ac4bdc2d470e8b456a")
            {
                item.Template = "5732ee6a24597719ae0c0281";
            }
        }
    }

    private void RagfairMinLevelOverride()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] Ragfail min level override enabled", LogTextColor.Cyan);
        databaseService.GetGlobals().Configuration.RagFair.MinUserLevel = config.RagFairMinUserLevel;
        Leaves.Utils.Instance.printColor($"[TreasureBox] \tRagfair min level overriden to: {config.RagFairMinUserLevel}", LogTextColor.Yellow);
    }

    private void Ak12Muzzle()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] AK 12 muzzle enabled", LogTextColor.Cyan);
        MongoId ak12 = "6499849fc93611967b034949";
        MongoId ak105 = "5ac66d9b5acfc4001633997a";

        var ak105Filters = Leaves.Utils.Instance.getSlotFilter(itemDB[ak105], 2);
        var ak12Filters = Leaves.Utils.Instance.getSlotFilter(itemDB[ak12], 2);

        int count = 0;
        foreach (var item in ak105Filters)
        {
            ak12Filters.Add(item);
            count++;
        }
        Leaves.Utils.Instance.printColor($"[TreasureBox] \tAdded this many new muzzles to the ak12: {count}", LogTextColor.Yellow);
    }

    // Direct assignment into the dictionary returned by LazyLoad<T>.Value would be silently
    // discarded - LazyLoad re-deserializes from disk (not cached) on every access, including
    // whatever serves locale text to the game client, so the mutation would just vanish the next
    // time anything reads that locale. AddTransformer (same mechanism as LootFuckery's loot
    // editing) is what actually makes this persist.
    private void FakeVodkaFix()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] Fuck Fake Vodka", LogTextColor.Cyan);
        MongoId fakeVodkaID = "614451b71e5874611e2c7ae5";

        Dictionary<string, string>? Transform(Dictionary<string, string>? locale)
        {
            if (locale == null)
            {
                return locale;
            }
            locale[$"{fakeVodkaID} Name"] = "Fake Vodka from Hell";
            locale[$"{fakeVodkaID} ShortName"] = "ASS Vodka";
            locale[$"{fakeVodkaID} Description"] =
                "What you've just found is one of the most insanely idiotic things someone has ever brewed. " +
                "At no point in it's awful, incoherent brewing was it even close to anything that could be considered a reasonable vodka. " +
                "Everyone in tarkov is now dumber for having tasted it. I award it no points, and may God have mercy on it's soul. ";
            return locale;
        }

        databaseService.GetLocales().Global["en"].AddTransformer(Transform);
    }

    // The "Thumb drive with military data" is a QUEST-item variant that looks identical to the ordinary
    // flash drive but spawns commonly as regular loot - easy to confuse. Recolour its inventory cell red
    // (a direct _props edit, unlike the vodka fix's locale transform) so it stands out at a glance.
    private void MilitaryFlashDriveFix()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] Military flash drive background", LogTextColor.Cyan);
        MongoId militaryFlashDriveID = "6889e4cd3ff7e9cfbc0c507c";
        if (!itemDB.TryGetValue(militaryFlashDriveID, out var item) || item.Properties == null)
        {
            Leaves.Utils.Instance.printColor("[TreasureBox] \tMilitary flash drive isn't in the database - skipped.", LogTextColor.Red);
            return;
        }
        item.Properties.BackgroundColor = "red";
        Leaves.Utils.Instance.printColor($"[TreasureBox] \t{item.Name} background set to red.", LogTextColor.Yellow);
    }

    private void RpdMods()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] RPD mods", LogTextColor.Cyan);
        //RPD and RPDN
        MongoId[] rpdIDs = ["6513ef33e06849f06c0957ca", "65268d8ecb944ff1e90ea385"];
        MongoId rpdLongBarrel = "6513eff1e06849f06c0957d4";
        MongoId rpdShortBarrel = "65266fd43341ed9aa903dd56";
        MongoId ak103 = "5ac66d2e5acfc43b321d4b53";
        MongoId gastubeID = "59c6633186f7740cf0493bb9";

        var ak103MuzzleFilters = Leaves.Utils.Instance.getSlotFilter(itemDB[ak103], 2);
        var rpdLongBarrelFilter = Leaves.Utils.Instance.getSlotFilter(itemDB[rpdLongBarrel], 1);
        var rpdShortBarrelFilter = Leaves.Utils.Instance.getSlotFilter(itemDB[rpdShortBarrel], 0);

        //Both barrels
        foreach (var item in ak103MuzzleFilters)
        {
            rpdLongBarrelFilter.Add(item);
            rpdShortBarrelFilter.Add(item);
        }

        var gastubeFilter = Leaves.Utils.Instance.getSlotFilter(itemDB[gastubeID], 0);

        //Both RPDs
        foreach (var weaponID in rpdIDs)
        {
            var handguardFilter = Leaves.Utils.Instance.getSlotFilter(itemDB[weaponID], 3);
            //Add hg to the rpd from the gastube
            foreach (var hgID in gastubeFilter)
            {
                handguardFilter.Add(hgID);
            }
        }
    }

    private void MemeUsp45()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] MemeUSP45", LogTextColor.Cyan);
        MongoId usp45ID = "6193a720f8ee7e52e42109ed";
        Leaves.Utils.Instance.getSlotFilter(itemDB[usp45ID], 6).Add("6357c98711fb55120211f7e1");
    }

    private void TraderRefresh()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] Trader Refresh Adjustment", LogTextColor.Cyan);

        int adjustmentMin = config.TraderRefreshTimeAdjustmentMin;
        int adjustmentMax = config.TraderRefreshTimeAdjustmentMax;
        int adjustmentLimit = config.TraderRefreshTimeAdjustmentLimit;

#pragma warning disable CS0618
        var traderConfig = configServer.GetConfig<TraderConfig>();
#pragma warning restore CS0618

        foreach (var trader in traderConfig.UpdateTime)
        {
            trader.Seconds.Min += adjustmentMin;
            trader.Seconds.Max += adjustmentMax;

            if (trader.Seconds.Min < adjustmentLimit)
            {
                trader.Seconds.Min = adjustmentLimit;
            }

            if (trader.Seconds.Max < adjustmentLimit)
            {
                trader.Seconds.Max = adjustmentLimit;
            }

            if (trader.Seconds.Min > trader.Seconds.Max)
            {
                trader.Seconds.Min = trader.Seconds.Max;
            }
        }
    }

    private void FenceBlacklist()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] Fence Blacklisting stuff", LogTextColor.Cyan);

#pragma warning disable CS0618
        var traderConfig = configServer.GetConfig<TraderConfig>();
#pragma warning restore CS0618

        foreach (var entry in config.FenceBlacklist)
        {
            Leaves.Utils.Instance.printColor($"[TreasureBox] \tBlacklisting: {entry}", LogTextColor.Yellow);
            traderConfig.Fence.Blacklist.Add(entry);
        }
    }

    private void SuppressorErgoAdjustment()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] Suppressor Ergo Adjustment", LogTextColor.Cyan);
        MongoId suppressorParent = "550aa4cd4bdc2dd8348b456c";
        foreach (var item in itemDB.Values)
        {
            if (item.Parent == suppressorParent)
            {
                item.Properties!.Ergonomics = (item.Properties.Ergonomics ?? 0) + config.SuppressorErgoAdjustment;
            }
        }
    }

    private void M700Barrel()
    {
        MongoId m700ID = "5bfea6e90db834001b7347f3";
        MongoId sa58Barrel = "5b099a765acfc47a8607efe3";
        Leaves.Utils.Instance.getSlotFilter(itemDB[m700ID], 2).Add(sa58Barrel);

        MongoId[] akmnMuzzles =
        [
            "59d64fc686f774171b243fe2", "5a0d716f1526d8000d26b1e2", "5f633f68f5750b524b45f112",
            "5c878ebb2e2216001219d48a", "59e61eb386f77440d64f5daf", "59e8a00d86f7742ad93b569c",
            "5a9ea27ca2750c00137fa672", "5cc9ad73d7f00c000e2579d4", "5c7951452e221644f31bfd5c",
            "615d8e9867085e45ef1409c6", "5a0abb6e1526d8000a025282", "59bffc1f86f77435b128b872",
            "593d489686f7745c6255d58a", "5a0d63621526d8dba31fe3bf", "5a9fbacda2750c00141e080f",
            "64942bfc6ee699f6890dff95"
        ];

        MongoId svdBarrel = "5c471cb32e221602b177afaa";
        var svdFilter = Leaves.Utils.Instance.getSlotFilter(itemDB[svdBarrel], 0);
        foreach (var muzzle in akmnMuzzles)
        {
            svdFilter.Add(muzzle);
        }
    }

    private void EnableQuests()
    {
#pragma warning disable CS0618
        var questConfig = configServer.GetConfig<QuestConfig>();
        var inventoryConfig = configServer.GetConfig<InventoryConfig>();
#pragma warning restore CS0618

        foreach (var questID in config.Quests)
        {
            questConfig.EventQuests.Remove(questID);
        }

        MongoId caseID = "674098588466ebb03408b210"; // CASE
        MongoId sealedCaseID = "674078c4a9c9adf0450d59f9";

        inventoryConfig.RandomLootContainers[caseID].RewardCount = 4;
        inventoryConfig.RandomLootContainers[caseID].RewardTypePool = new HashSet<MongoId>
        {
            "57864c8c245977548867e7f1",
            "54009119af1c881c07000029",
            "57864c322459775490116fbf",
            "57864a3d24597754843f8721",
            "57864ee62459775490116fc1",
            "57864e4c24597754843f8723",
            "55818ae44bdc2dde698b456c",
            "550aa4154bdc2dd8348b456b"
        };

        inventoryConfig.RandomLootContainers[sealedCaseID] = cloner.Clone(inventoryConfig.RandomLootContainers[caseID])!;
    }

    private void Junkbox()
    {
        var therapistAssort = databaseService.GetTraders()["54cb57776803fa99248b456e"].Assort;

        MongoId junkboxTpl = "5b7c710788a4506dec015957"; // Lucky Scav Junkbox
        // Every dogtag barter in the game pays with THIS one tpl - the USEC dogtag is BSG's placeholder
        // meaning "any dogtag", so the BEAR tpl never appears in an assort. Match only it.
        MongoId anyDogtag = "59f32c3b86f77472a31742f0";

        // The barter's assort ENTRY id changes every SPT version (66e8031f... in 3.10, 676d24cf... in
        // 3.11, 686e3471... in 4.0.13), so hardcoding it silently breaks on each update - which is
        // exactly what happened here (both old ids were gone in 4.0.13, so this did nothing and the
        // price stayed 40). Find the barter by WHAT IT IS instead: a top-level Lucky Scav Junkbox
        // entry paid for in dogtags. The junkbox also has a separate cash entry; keying on the dogtag
        // term skips it. Robust to the id churning again next update.
        int changed = 0;
        foreach (var item in therapistAssort.Items)
        {
            if (item.Template != junkboxTpl || item.ParentId != "hideout")
            {
                continue;
            }
            if (!therapistAssort.BarterScheme.TryGetValue(item.Id, out var scheme))
            {
                continue;
            }
            foreach (var group in scheme)
            {
                foreach (var term in group)
                {
                    if (term.Template == anyDogtag)
                    {
                        term.Count = 80;
                        changed++;
                    }
                }
            }
        }

        Leaves.Utils.Instance.printColor(
            changed > 0
                ? $"[TreasureBox] Lucky Scav Junkbox dogtag cost set to 80 ({changed} barter term(s) updated)."
                : "[TreasureBox] Junkbox: no dogtag barter found to retune - has the junkbox tpl or Therapist assort changed?",
            changed > 0 ? LogTextColor.Cyan : LogTextColor.Red);
    }

    private void BtrChanges()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] Making BTR always appear. In theory.");
        var btrSettings = databaseService.GetLocationServices().BtrServerSettings;

        btrSettings.ChanceSpawn = 100;
        btrSettings.SpawnPeriod.X = 120;
        btrSettings.SpawnPeriod.Y = 180;

        btrSettings.ServerMapBTRSettings["TarkovStreets"].ChanceSpawn = 100;
        btrSettings.ServerMapBTRSettings["TarkovStreets"].SpawnPeriod.X = 120;
        btrSettings.ServerMapBTRSettings["TarkovStreets"].SpawnPeriod.Y = 180;

        btrSettings.ServerMapBTRSettings["Woods"].ChanceSpawn = 100;
        btrSettings.ServerMapBTRSettings["Woods"].SpawnPeriod.X = 120;
        btrSettings.ServerMapBTRSettings["Woods"].SpawnPeriod.Y = 180;
    }

    private void ChangeKeys()
    {
        MongoId keyMechanical = "5c99f98d86f7745c314214b3";
        MongoId keycard = "5c164d2286f774194c5e69fa";

        foreach (var item in itemDB.Values)
        {
            if (item.Parent == keyMechanical || item.Parent == keycard)
            {
                item.Properties!.MaximumNumberOfUsage = GetNewUses(item.Properties.MaximumNumberOfUsage ?? 0);
            }
        }
    }

    private int GetNewUses(int uses)
    {
        if (uses == 40)
        {
            return 0;
        }
        if (uses == 10)
        {
            return 5;
        }
        return uses;
    }

    // Lets the DrakiaXYZ-GildedKeyStorage containers (the golden keychains + keycard holder) be stored
    // inside the vanilla Key Box. The Key Box's single grid has a whitelist Filter that already allows
    // the Keys category and a handful of keychains, but the golden items are custom containers parented
    // elsewhere, so each has to be added to that whitelist by id. We run at PostDBModLoader+1, after
    // GildedKeyStorage has injected its items, so a plain itemDB presence check is enough - any id that
    // isn't there (mod not installed, or an id changed) is skipped rather than blindly added.
    private void GildedKeysInKeyBox()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] Adding Gilded Key Storage items to the Key Box.", LogTextColor.Cyan);

        MongoId keyBoxID = "67d3ed3271c17ff82e0a5b0b"; // vanilla Key Box (item_container_keys_case)
        if (!itemDB.TryGetValue(keyBoxID, out var keyBox))
        {
            Leaves.Utils.Instance.printColor("[TreasureBox] \tKey Box isn't in the database - skipped.", LogTextColor.Red);
            return;
        }

        var keyBoxFilter = Leaves.Utils.Instance.getGridFilter(keyBox, 0);

        int added = 0;
        foreach (var goldenID in config.GildedKeysInKeyBoxItems)
        {
            if (!itemDB.ContainsKey(goldenID))
            {
                Leaves.Utils.Instance.printColor($"[TreasureBox] \t{goldenID} isn't in the database (GildedKeyStorage not installed?) - skipped.", LogTextColor.Red);
                continue;
            }
            keyBoxFilter.Add(goldenID);
            added++;
            Leaves.Utils.Instance.printColor($"[TreasureBox] \tAllowed {Leaves.Utils.Instance.getLocale("en", goldenID.ToString(), " Name") ?? goldenID.ToString()} in the Key Box.", LogTextColor.Yellow);
        }

        Leaves.Utils.Instance.printColor($"[TreasureBox] \tAdded {added} Gilded Key Storage item(s) to the Key Box.", LogTextColor.Cyan);
    }

    // Goes through every magazine and zeroes out any reload-speed PENALTY, keeping bonuses. A
    // magazine's LoadUnloadModifier is a percent modifier on load/unload speed - positive = slower
    // (drum mags, oversized mags), negative = faster. We floor positives to 0 and leave negatives
    // alone, so a magazine can only ever be as good as or better than vanilla, never worse.
    public void MagazineLoadSpeed()
    {
        //Re-fetch the db to catch mod items. (nevermind lol)
        //itemDB = databaseService.GetItems();
        if (!config.RemoveMagazineLoadPenalty)
        {
            return;
        }
        Leaves.Utils.Instance.printColor("[TreasureBox] Removing magazine reload-speed penalties.", LogTextColor.Cyan);
        MongoId magazineParent = "5448bc234bdc2d3c308b4569"; // Magazine category
        int changed = 0;
        foreach (var item in itemDB.Values)
        {
            if (item.Parent != magazineParent || item.Properties == null)
            {
                continue;
            }
            if (item.Properties.LoadUnloadModifier is { } modifier && modifier > 0)
            {
                item.Properties.LoadUnloadModifier = 0;
                changed++;
            }
        }
        Leaves.Utils.Instance.printColor($"[TreasureBox] \tCleared the reload penalty on {changed} magazine(s).", LogTextColor.Yellow);
    }

    private void BitcoinPrice()
    {
        Leaves.Utils.Instance.printColor("[TreasureBox] Making BITCOINS cheaper");
        MongoId bitcoinID = "59faff1d86f7746c51718c9c";
        // Guard against a zero/negative divisor in config dividing by zero (or inflating the price) -
        // fall back to 1 (leave the price as-is) in that case.
        double divisor = config.BitcoinPriceDivisor;
        if (divisor <= 0)
        {
            divisor = 1;
        }
        var bitcoin = databaseService.GetHandbook().Items.First(x => x.Id == bitcoinID);
        Leaves.Utils.Instance.printColor("Price before: " + bitcoin.Price);
        bitcoin.Price = Math.Floor((bitcoin.Price ?? 0) / divisor);
        Leaves.Utils.Instance.printColor("Price after: " + bitcoin.Price);
    }
}

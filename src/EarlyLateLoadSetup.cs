using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Services;

namespace Leaves_TreasureBox;

// Two shims that exist purely for their LOAD STAGE - a single [Injectable] can't register more than
// one OnLoad, and TreasureBox's own OnLoad has to stay where it is. Both just call into the
// TreasureBox singleton; see FixModAmmoPricing there for what actually happens.

// Captures the item table before any mod has added to it. SPT loads the database at the Database
// stage (200000) and mods add their items at PostDBModLoader (400000+), so this is the one moment
// the table is guaranteed pure vanilla - which is how the ammo fix tells "shipped with the game"
// (the pricing reference) from "added by a mod" (the thing being corrected).
[Injectable(TypePriority = OnLoadOrder.Database + 1)]
public class EarlyOnLoadSetup(TreasureBox treasureBox, DatabaseService databaseService) : IOnLoad
{
    public Task OnLoad()
    {
        treasureBox.CaptureVanillaItemIds(databaseService.GetItems().Keys);
        return Task.CompletedTask;
    }
}

// Runs the mod-ammo price fix inside a narrow window, bounded on BOTH sides:
//   AFTER  PostDBModLoader+2 (400002) - content mods add their ammo there, and we can't reprice
//          what doesn't exist yet. TreasureBox's own OnLoad (PostDBModLoader+1) is too early.
//   BEFORE HandbookCallbacks (600000) - HandbookHelper hydrates a price cache
//          (LookupCollection _handbookPriceCache) there. A handbook price changed afterwards is
//          simply not seen by GetTemplatePrice, so the fix would silently do nothing. (This is the
//          same class of trap that broke sealed weapon cases via PresetCallbacks - see
//          Leaves-ShinyAirdropGuns' HANDOFF §2.1.)
// PostDBModLoader+90000 (490000) sits in that window: the PostDBModLoader stage runs 400000-499999,
// so +90000 is after every mod in it, and still well below 600000.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 90000)]
public class LateOnLoadSetup(TreasureBox treasureBox) : IOnLoad
{
    public Task OnLoad()
    {
        treasureBox.FixModAmmoPricing();
        treasureBox.MagazineLoadSpeed();
        return Task.CompletedTask;
    }
}

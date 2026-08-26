using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Dialogue;
using SPTarkov.Server.Core.Helpers.Dialogue.Commando;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Dialog;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

namespace Leaves_TreasureBox;

// Whitelists WHO may use the "Commando" in-game chatbot - the dev bot behind `spt give`, `spt trader`
// and `spt profile`. On a shared Fika coop server the vanilla bot answers anyone, which is a wide-open
// item/level cheat; this gates the whole bot to a configured list of profile ids.
//
// HOW THE OVERRIDE WORKS (this is NOT one of TreasureBox's OnLoad database tweaks):
// SPT's DI (see SPTarkov.DI.DependencyInjectionHandler.InjectAll) treats a [Injectable] whose
// TypeOverride names another type as a REPLACEMENT: the named type's full name is dropped from the
// registration set, and this class is registered under the interfaces/base types it inherits instead.
// So by subclassing CommandoDialogChatBot and pointing TypeOverride at it, the stock bot is removed and
// this one is resolved in its place as the sole IDialogueChatBot for "Commando" - no duplicate-key
// clash (which is what a second bot would otherwise cause downstream). All of CommandoDialogChatBot's
// members are virtual and its constructor is public, so a plain subclass is enough - no Harmony.
//
// The config lives in TreasureBox's own config.jsonc (commandoWhitelistEnabled + commandoWhitelist).
// This service is constructed independently of the TreasureBox OnLoad tweak, so it loads that file
// itself rather than sharing state with the TreasureBox instance.
[Injectable(InjectionType.Scoped, typeof(CommandoDialogChatBot), int.MaxValue)]
public class CommandoWhitelist : CommandoDialogChatBot
{
    private readonly MailSendService _mailSendService;
    private readonly bool _whitelistEnabled;
    private readonly HashSet<MongoId> _whitelist;

    public CommandoWhitelist(
        ISptLogger<AbstractDialogChatBot> logger,
        MailSendService mailSendService,
        ServerLocalisationService localisationService,
#pragma warning disable CS0618 // ConfigServer is obsolete but is what the base bot's constructor takes in 4.0.13.
        ConfigServer configServer,
#pragma warning restore CS0618
        IEnumerable<ICommandoCommand> chatCommands,
        ModHelper modHelper)
        : base(logger, mailSendService, localisationService, configServer, chatCommands)
    {
        _mailSendService = mailSendService;

        // Read the same config.jsonc TreasureBox's OnLoad tweak reads. The file is always present on
        // disk (it's deployed with the mod), so this has no ordering dependency on OnLoad.
        string modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        TreasureBoxConfig config = modHelper.GetJsonDataFromFile<TreasureBoxConfig>(modPath, "config/config.jsonc");

        _whitelistEnabled = config.CommandoWhitelistEnabled;

        _whitelist = new HashSet<MongoId>();
        foreach (MongoId allowedProfileId in config.CommandoWhitelist)
        {
            _whitelist.Add(allowedProfileId);
        }
    }

    // Single choke point for the whole bot: every "Commando" chat message flows through here, so gating
    // it covers give, trader and profile at once. sessionId IS the sender's profile id (info.id).
    public override ValueTask<string> HandleMessage(MongoId sessionId, SendMessageRequest request)
    {
        if (_whitelistEnabled && !_whitelist.Contains(sessionId))
        {
            _mailSendService.SendUserMessageToPlayer(
                sessionId,
                GetChatBot(),
                "You are not authorized to use the Commando bot on this server.",
                new List<Item>(),
                null);
            return new ValueTask<string>(request.DialogId);
        }

        return base.HandleMessage(sessionId, request);
    }
}

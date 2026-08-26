using System.Collections.Generic;
using System.Reflection;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace Leaves_TreasureBox;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "Leaves.You.Alone.TreasureBox";
    public override string Name { get; init; } = "TreasureBox";
    public override string Author { get; init; } = "DeadLeaves";
    public override List<string>? Contributors { get; init; } = new List<string> { "Leaves" };
    public override SemanticVersioning.Version Version { get; init; } = new(GetAssemblyVersion());
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new Dictionary<string, SemanticVersioning.Range>
    {
        { "Leaves.You.Alone.LeavesUtils", new SemanticVersioning.Range("^1.0.0") }
    };
    public override string? Url { get; init; } = "https://github.com/DeadLeavez/Leaves-TreasureBox";
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";

    private static string GetAssemblyVersion()
    {
        return Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.0.0";
    }
}

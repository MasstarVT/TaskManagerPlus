namespace TaskManagerPlus.Models;

/// <summary>The on-disk JSON shape of one file under AppPaths.SettingsDirectory\Rules\ - the
/// built-in pack, a user's custom/edited-rule file (user-overrides.json, #922), or an
/// imported/exported pack (#926). One file, one PackName, any number of Rules.</summary>
public sealed class RulePackFile
{
    public string PackName { get; set; } = string.Empty;
    public List<Rule> Rules { get; set; } = new();
}

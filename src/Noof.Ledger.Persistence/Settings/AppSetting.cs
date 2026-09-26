namespace Noof.Ledger.Persistence.Settings;

internal sealed class AppSetting
{
    public required string Key { get; init; }
    public required string Value { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}

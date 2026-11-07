using Microsoft.AspNetCore.Components;
using SteamDB.Core;
using SteamDB.Infrastructure;

namespace SteamDB.Web.Components;

public static class Fmt
{
    public static string N(double? value, string format = "0") => value is null ? "—" : value.Value.ToString(format);
    public static string I(int? value) => value is null ? "—" : value.Value.ToString("N0");
    public static string P(double? value) => value is null ? "—" : value.Value.ToString("P1");
    public static string Signed(double? value) => value is null ? "—" : value.Value.ToString("+0.0%;-0.0%;0.0%");
    public static string Money(int? cents) => cents is null ? "—" : (cents.Value / 100m).ToString("$#,0.00");
    public static string Chance(ProportionView? view) => view is null ? "—" : $"{view.Point:P1} ({view.Lower:P1}–{view.Upper:P1})";
}

public class AnalyticsPage : ComponentBase, IDisposable
{
    [Inject] protected IDatasetCache Cache { get; set; } = default!;
    [Inject] protected ISettingsStore Settings { get; set; } = default!;

    protected DatasetSnapshot Snap => Cache.Current;
    protected AnalysisWindow Window => Settings.Current.ToWindow();
    protected DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    protected override void OnInitialized()
    {
        Cache.Changed += OnChanged;
        Settings.Changed += OnChanged;
    }

    private void OnChanged() => _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Cache.Changed -= OnChanged;
        Settings.Changed -= OnChanged;
    }
}

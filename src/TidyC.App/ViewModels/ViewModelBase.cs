using CommunityToolkit.Mvvm.ComponentModel;
using TidyC.App.Services;
using TidyC.Core.Rules;

namespace TidyC.App.ViewModels;

/// <summary>Navigation and shell actions the child view models may request.</summary>
public interface IAppShell
{
    void Navigate(string page);

    Task ScanAsync();

    void StartCleanup(IReadOnlyList<CleanupCandidate>? candidates);

    void ShowToast(string message);
}

public abstract class ViewModelBase : ObservableObject
{
    protected static AppServices Services => AppServices.Current;

    protected static string Size(long bytes) => TidyC.Core.Sys.ByteSize.Format(bytes);

    protected static string SizeSigned(long bytes) => TidyC.Core.Sys.ByteSize.FormatSigned(bytes);

    protected static string Count(long count) => count.ToString("N0");
}

public sealed class NavigationItem
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public required string Glyph { get; init; }

    public required string Hint { get; init; }

    public bool IsNew { get; init; }
}

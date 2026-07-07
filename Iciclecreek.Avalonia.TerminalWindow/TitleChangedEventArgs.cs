using Avalonia.Interactivity;

namespace Iciclecreek.Terminal;

/// <summary>
///     EventArgs for the TitleChanged event.
/// </summary>
public class TitleChangedEventArgs(string title) : RoutedEventArgs
{
    public string Title { get; } = title;
}
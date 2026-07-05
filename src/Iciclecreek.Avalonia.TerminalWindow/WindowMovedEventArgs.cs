using Avalonia.Interactivity;

namespace Iciclecreek.Terminal;

/// <summary>
///     EventArgs for the WindowMoved event.
/// </summary>
public class WindowMovedEventArgs(int x, int y) : RoutedEventArgs
{
    public int X { get; } = x;
    public int Y { get; } = y;
}
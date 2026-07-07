using Avalonia.Interactivity;

namespace Iciclecreek.Terminal;

/// <summary>
///     EventArgs for the WindowResized event.
/// </summary>
public class WindowResizedEventArgs(int width, int height) : RoutedEventArgs
{
    public int Width { get; } = width;
    public int Height { get; } = height;
}
using System;

namespace Iciclecreek.Terminal;

/// <summary>
///     EventArgs for the ProcessExited event.
/// </summary>
public class ProcessExitedEventArgs(int exitCode) : EventArgs
{
    public int ExitCode { get; } = exitCode;
}
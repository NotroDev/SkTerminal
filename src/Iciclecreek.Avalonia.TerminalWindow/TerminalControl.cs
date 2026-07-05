using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using XTerm.Options;

namespace Iciclecreek.Terminal;

public sealed class TerminalControl : TemplatedControl
{
    public static readonly StyledProperty<TextDecorationLocation?> TextDecorationsProperty =
        AvaloniaProperty.Register<TerminalControl, TextDecorationLocation?>(
            nameof(TextDecorations));

    public static readonly StyledProperty<IBrush> SelectionBrushProperty =
        AvaloniaProperty.Register<TerminalControl, IBrush>(
            nameof(SelectionBrush),
            new SolidColorBrush(Color.FromArgb(128, 0, 120, 215)));

    public static readonly StyledProperty<string> ProcessProperty =
        AvaloniaProperty.Register<TerminalControl, string>(
            nameof(Process),
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "bash");

    public static readonly StyledProperty<IList<string>> ArgsProperty =
        AvaloniaProperty.Register<TerminalControl, IList<string>>(
            nameof(Args),
            Array.Empty<string>());

    public static readonly StyledProperty<string?> StartingDirectoryProperty =
        AvaloniaProperty.Register<TerminalControl, string?>(
            nameof(StartingDirectory));

    public static readonly DirectProperty<TerminalControl, string?> CurrentDirectoryProperty =
        AvaloniaProperty.RegisterDirect<TerminalControl, string?>(
            nameof(CurrentDirectory),
            o => o.CurrentDirectory);

    public static readonly StyledProperty<int> BufferSizeProperty =
        AvaloniaProperty.Register<TerminalControl, int>(
            nameof(BufferSize),
            1000);

    public static readonly StyledProperty<TerminalOptions?> OptionsProperty =
        AvaloniaProperty.Register<TerminalControl, TerminalOptions?>(
            nameof(Options));

    public static readonly StyledProperty<bool> CloseProcessOnDetachProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(
            nameof(CloseProcessOnDetach),
            true);

    private string? _currentDirectory;
    private ScrollBar? _scrollBar;
    private TerminalView? _terminalView;

    static TerminalControl()
    {
        // TerminalControl is focusable - it will delegate to inner TerminalView
        FocusableProperty.OverrideDefaultValue<TerminalControl>(true);
    }

    public bool CloseProcessOnDetach
    {
        get => GetValue(CloseProcessOnDetachProperty);
        set => SetValue(CloseProcessOnDetachProperty, value);
    }

    /// <summary>
    ///     Gets or sets the brush used to render selected terminal text.
    /// </summary>
    public IBrush SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    /// <summary>
    ///     Gets or sets the executable or shell to launch in the terminal.
    /// </summary>
    public string Process
    {
        get => GetValue(ProcessProperty);
        set => SetValue(ProcessProperty, value);
    }

    /// <summary>
    ///     Gets or sets the command-line arguments passed to <see cref="Process" /> when launching.
    /// </summary>
    public IList<string> Args
    {
        get => GetValue(ArgsProperty);
        set => SetValue(ArgsProperty, value);
    }

    /// <summary>
    ///     Gets or sets the initial working directory used when the PTY process is started.
    /// </summary>
    public string? StartingDirectory
    {
        get => GetValue(StartingDirectoryProperty);
        set => SetValue(StartingDirectoryProperty, value);
    }

    /// <summary>
    ///     Gets the current working directory reported by the running terminal session.
    /// </summary>
    public string? CurrentDirectory => _currentDirectory;

    /// <summary>
    ///     Gets or sets the terminal scrollback buffer size in lines.
    /// </summary>
    public int BufferSize
    {
        get => GetValue(BufferSizeProperty);
        set => SetValue(BufferSizeProperty, value);
    }

    /// <summary>
    ///     Gets or sets the terminal emulation options used by the inner <see cref="TerminalView" />.
    /// </summary>
    public TerminalOptions? Options
    {
        get => GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    /// <summary>
    ///     Gets the underlying <see cref="XTerm.Terminal" /> instance.
    /// </summary>
    public XTerm.Terminal Terminal => _terminalView!.Terminal;

    /// <inheritdoc cref="TerminalView.ShowCaretOnClickProperty" />
    public bool ShowCaretOnClick
    {
        get => _terminalView?.ShowCaretOnClick ?? false;
        set
        {
            if (_terminalView != null)
            {
                _terminalView.ShowCaretOnClick = value;
            }
        }
    }

    /// <summary>
    ///     Gets the exit code of the launched process after it has terminated.
    /// </summary>
    public int ExitCode => _terminalView!.ExitCode;

    /// <summary>
    ///     Gets the operating system process identifier of the launched terminal process.
    /// </summary>
    public int Pid => _terminalView!.Pid;

    public event EventHandler<ProcessExitedEventArgs>? ProcessExited;


    /// <summary>
    ///     Waits for the terminal process to exit, with a timeout in milliseconds.
    /// </summary>
    /// <param name="ms">The maximum amount of time to wait, in milliseconds.</param>
    public void WaitForExit(int ms)
    {
        _terminalView!.WaitForExit(ms);
    }

    /// <summary>
    ///     Terminates the running terminal process.
    /// </summary>
    public void Kill()
    {
        _terminalView!.Kill();
    }

    /// <summary>
    ///     Call before removing this control from one visual tree and adding it to another
    ///     (e.g. moving between windows). Prevents the PTY process from being killed
    ///     during the detach. Pair with <see cref="EndReparent" /> after re-attaching.
    /// </summary>
    public void BeginReparent()
    {
        _terminalView?.BeginReparent();
    }

    /// <summary>
    ///     Call after the control has been re-attached to a new visual tree to restore
    ///     normal cleanup behaviour.
    /// </summary>
    public void EndReparent()
    {
        _terminalView?.EndReparent();
    }

    /// <summary>
    ///     Launch the terminal process with the current Process, Args, and StartingDirectory properties. If the process is
    ///     already running, it will be
    ///     terminated and replaced with a new instance using the updated properties.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task LaunchProcess()
    {
        if (_terminalView == null)
        {
            ApplyTemplate();
        }

        if (_terminalView == null)
        {
            throw new InvalidOperationException("TerminalControl template has not been applied yet.");
        }

        await _terminalView.LaunchProcess();

        Dispatcher.UIThread.Post(() =>
        {
            if (_terminalView is { IsFocused: false })
            {
                _terminalView.Focus();
            }
        }, DispatcherPriority.Input);
    }

    /// <summary>
    ///     Launch the terminal process with the specified parameters, updating the Process, Args, and StartingDirectory
    ///     properties.
    ///     If the process is already running, it will be terminated and replaced with a new instance using the updated
    ///     properties.
    /// </summary>
    /// <param name="startingDirectory"></param>
    /// <param name="process"></param>
    /// <param name="args"></param>
    /// <returns></returns>
    public async Task LaunchProcess(string? startingDirectory, string process, params string[]? args)
    {
        StartingDirectory = startingDirectory;
        Process = process;
        Args = args ?? [];
        await LaunchProcess();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);

        // Only focus the inner TerminalView if it doesn't already have focus
        if (_terminalView is { IsFocused: false })
        {
            // Defer until layout is ready
            Dispatcher.UIThread.Post(() =>
            {
                if (_terminalView is { IsFocused: false })
                {
                    _terminalView.Focus();
                }
            }, DispatcherPriority.Input);
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        // Unsubscribe from old controls
        if (_scrollBar != null)
        {
            _scrollBar.Scroll -= OnScrollBarScroll;
        }

        if (_terminalView != null)
        {
            _terminalView.PropertyChanged -= OnTerminalViewPropertyChanged;
            _terminalView.ProcessExited -= OnTerminalViewProcessExited;
        }

        SetCurrentDirectory(null);

        // Get template parts
        _terminalView = e.NameScope.Find<TerminalView>("PART_TerminalView");
        _scrollBar = e.NameScope.Find<ScrollBar>("PART_ScrollBar");

        // Wire up scrollbar
        if (_scrollBar != null && _terminalView != null)
        {
            _scrollBar.Scroll += OnScrollBarScroll;
            _terminalView.Options = Options ?? new TerminalOptions();
            _terminalView.PropertyChanged += OnTerminalViewPropertyChanged;
            _terminalView.ProcessExited += OnTerminalViewProcessExited;
            SetCurrentDirectory(_terminalView.CurrentDirectory);
            // (no window event hooking needed)

            _terminalView.Bind(TerminalView.CloseProcessOnDetachProperty,
                this.GetObservable(CloseProcessOnDetachProperty));
        }
    }

    private void OnScrollBarScroll(object? sender, ScrollEventArgs e)
    {
        if (_terminalView != null)
        {
            _terminalView.ViewportY = (int)e.NewValue;
        }
    }

    private void OnTerminalViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TerminalView.MaxScrollbackProperty ||
            e.Property == TerminalView.ViewportLinesProperty ||
            e.Property == TerminalView.ViewportYProperty ||
            e.Property == TerminalView.IsAlternateBufferProperty)
        {
            UpdateScrollBar();
        }
        else if (e.Property == TerminalView.CurrentDirectoryProperty)
        {
            SetCurrentDirectory(_terminalView?.CurrentDirectory);
        }
    }

    private void OnTerminalViewProcessExited(object? sender, ProcessExitedEventArgs e)
    {
        ProcessExited?.Invoke(this, e);
    }

    private void SetCurrentDirectory(string? currentDirectory)
    {
        SetAndRaise(CurrentDirectoryProperty, ref _currentDirectory, currentDirectory);
    }

    private void UpdateScrollBar()
    {
        if (_scrollBar == null || _terminalView == null)
        {
            return;
        }

        if (_terminalView.IsAlternateBuffer)
        {
            _scrollBar.IsVisible = false;
            _scrollBar.Value = 0;
            return;
        }

        int maxScrollback = _terminalView.MaxScrollback;
        int viewportLines = _terminalView.ViewportLines;
        int currentScroll = _terminalView.ViewportY;

        // Scrollbar range: 0 (top of buffer) to maxScrollback (bottom/current output)
        _scrollBar.Minimum = 0;
        _scrollBar.Maximum = maxScrollback;
        _scrollBar.ViewportSize = viewportLines;
        _scrollBar.Value = currentScroll;
        _scrollBar.IsVisible = maxScrollback > 0;
    }

    /// <summary>
    ///     Clears the terminal screen and scrollback buffer.
    /// </summary>
    public Task ClearAsync()
    {
        return _terminalView?.ClearAsync() ?? Task.CompletedTask;
    }
}
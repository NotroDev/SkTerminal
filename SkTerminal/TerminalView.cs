using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Avalonia.Terminal;
using Porta.Pty;
using SkTerminal;
using XTerm.Buffer;
using XTerm.Common;
using XTerm.Events;
using XTerm.Input;
using XTerm.Options;
using Key = Avalonia.Input.Key;
using KeyModifiers = Avalonia.Input.KeyModifiers;
using MouseButton = Avalonia.Input.MouseButton;
using SelectionMode = XTerm.Selection.SelectionMode;
using XT = XTerm;

namespace Iciclecreek.Terminal;

public sealed class TerminalView : Control
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static readonly DirectProperty<TerminalView, bool> IsAlternateBufferProperty =
        AvaloniaProperty.RegisterDirect<TerminalView, bool>(
            nameof(IsAlternateBuffer),
            o => o.IsAlternateBuffer);

    public static readonly DirectProperty<TerminalView, int> BufferSizeProperty =
        AvaloniaProperty.RegisterDirect<TerminalView, int>(
            nameof(BufferSize),
            o => o._bufferSize,
            (o, v) => o._bufferSize = v);

    public static readonly DirectProperty<TerminalView, int> ViewportYProperty =
        AvaloniaProperty.RegisterDirect<TerminalView, int>(
            nameof(ViewportY),
            o => o.ViewportY,
            (o, v) => o.ViewportY = v);

    public static readonly DirectProperty<TerminalView, int> MaxScrollbackProperty =
        AvaloniaProperty.RegisterDirect<TerminalView, int>(
            nameof(MaxScrollback),
            o => o.MaxScrollback);

    public static readonly DirectProperty<TerminalView, int> ViewportLinesProperty =
        AvaloniaProperty.RegisterDirect<TerminalView, int>(
            nameof(ViewportLines),
            o => o.ViewportLines);

    public static readonly DirectProperty<TerminalView, string?> CurrentDirectoryProperty =
        AvaloniaProperty.RegisterDirect<TerminalView, string?>(
            nameof(CurrentDirectory),
            o => o.CurrentDirectory);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<TerminalView, FontFamily>(
            nameof(FontFamily),
            FontFamily.Default);

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalView, double>(
            nameof(FontSize),
            12);

    public static readonly StyledProperty<FontStyle> FontStyleProperty =
        AvaloniaProperty.Register<TerminalView, FontStyle>(
            nameof(FontStyle));

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<TerminalView, FontWeight>(
            nameof(FontWeight),
            FontWeight.Normal);

    public static readonly StyledProperty<TextDecorationLocation?> TextDecorationsProperty =
        AvaloniaProperty.Register<TerminalView, TextDecorationLocation?>(
            nameof(TextDecorations));

    public static readonly StyledProperty<FontFeatureCollection> FontFeaturesProperty =
        AvaloniaProperty.Register<TerminalView, FontFeatureCollection>(
            nameof(FontFeatures));

    public static readonly StyledProperty<IBrush> ForegroundProperty =
        AvaloniaProperty.Register<TerminalView, IBrush>(
            nameof(Foreground),
            Brushes.White);

    public static readonly StyledProperty<IBrush> BackgroundProperty =
        AvaloniaProperty.Register<TerminalView, IBrush>(
            nameof(Background),
            Brushes.Black);

    public static readonly StyledProperty<IBrush> SelectionBrushProperty =
        AvaloniaProperty.Register<TerminalView, IBrush>(
            nameof(SelectionBrush),
            new SolidColorBrush(Color.FromArgb(128, 0, 120, 215)));

    public static readonly StyledProperty<string> ProcessProperty =
        AvaloniaProperty.Register<TerminalView, string>(
            nameof(Process),
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "bash");

    public static readonly StyledProperty<IList<string>> ArgsProperty =
        AvaloniaProperty.Register<TerminalView, IList<string>>(
            nameof(Args),
            Array.Empty<string>());

    public static readonly StyledProperty<string?> StartingDirectoryProperty =
        AvaloniaProperty.Register<TerminalView, string?>(
            nameof(StartingDirectory),
            Environment.CurrentDirectory);

    public static readonly StyledProperty<Color> CursorColorProperty =
        AvaloniaProperty.Register<TerminalView, Color>(
            nameof(CursorColor),
            Colors.White);

    public static readonly StyledProperty<CursorStyle> CursorStyleProperty =
        AvaloniaProperty.Register<TerminalView, CursorStyle>(
            nameof(CursorStyle),
            CursorStyle.Bar);

    public static readonly StyledProperty<bool> CursorBlinkProperty =
        AvaloniaProperty.Register<TerminalView, bool>(
            nameof(CursorBlink),
            true);

    public static readonly StyledProperty<int> CursorBlinkRateProperty =
        AvaloniaProperty.Register<TerminalView, int>(
            nameof(CursorBlinkRate),
            530);

    /// <summary>
    ///     When <see langword="false" /> (default), a plain single left-click does not
    ///     immediately show a selection highlight. The selection only starts once the
    ///     pointer moves, so casual clicks produce no visible caret artifact.
    ///     Set to <see langword="true" /> to restore the original behaviour where a
    ///     single-cell highlight appears on every click.
    ///     Double- and triple-click (word / line selection) are unaffected by this setting.
    /// </summary>
    public static readonly StyledProperty<bool> ShowCaretOnClickProperty =
        AvaloniaProperty.Register<TerminalView, bool>(
            nameof(ShowCaretOnClick));

    public static readonly StyledProperty<TerminalOptions?> OptionsProperty =
        AvaloniaProperty.Register<TerminalControl, TerminalOptions?>(
            nameof(Options));

    public static readonly StyledProperty<bool> CloseProcessOnDetachProperty =
        AvaloniaProperty.Register<TerminalView, bool>(
            nameof(CloseProcessOnDetach),
            true);

    // macOS uses the Command (⌘ / Meta) key for clipboard shortcuts, following native
    // platform conventions (Terminal.app, iTerm2, etc.). Windows and Linux terminals use
    // Ctrl+Shift+C / Ctrl+Shift+V instead, because plain Ctrl+C is reserved for SIGINT.
    private static readonly bool IsMacOs = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    // Unique identifier for this terminal instance (for debugging)
    private readonly Guid _instanceId = Guid.NewGuid();

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Lock _terminalLock = new(); // Serialises all _terminal.Write/WriteLine calls
    private int _bufferSize = 1000;
    private double _charHeight;
    private double _charWidth;
    private string? _currentDirectory;
    private bool _cursorBlinkOn = true;

    // Cursor blinking
    private DispatcherTimer _cursorBlinkTimer;

    // IME (Input Method Editor) support
    private TerminalInputMethodClient? _inputMethodClient;

    // Selection state - tracks whether terminal is handling selection vs forwarding mouse to app
    private bool _isSelecting;

    private FormattedText _measureText;

    // When non-null, a single left-click has been pressed but the selection hasn't started yet.
    // Selection start is deferred until pointer movement so that a plain click doesn't show a caret.
    private (int Col, int Row)? _pendingSelectionStart;
    private CancellationTokenSource? _processCts;
    private int _processExitHandled; // 0=false, 1=true — written via Interlocked

    // Process management
    private IPtyConnection? _ptyConnection;

    // When true, OnDetachedFromLogicalTree skips CleanupProcess so the PTY
    // survives a visual-tree re-parent (e.g. floating window pop-out/dock-back).
    private bool _suppressCleanupOnDetach;


    static TerminalView()
    {
        AffectsRender<TerminalView>(
            FontFamilyProperty,
            FontSizeProperty,
            FontStyleProperty,
            FontWeightProperty,
            TextDecorationsProperty,
            FontFeaturesProperty,
            ForegroundProperty,
            BackgroundProperty,
            SelectionBrushProperty,
            BufferSizeProperty,
            ViewportYProperty,
            CursorColorProperty,
            CursorStyleProperty,
            CursorBlinkProperty);

        AffectsMeasure<TerminalView>(
            FontFamilyProperty,
            FontSizeProperty,
            FontStyleProperty,
            FontWeightProperty,
            FontFeaturesProperty,
            BufferSizeProperty);

        FocusableProperty.OverrideDefaultValue<TerminalView>(true);
    }

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
    public TerminalView()
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
    {
        Focusable = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        TextInputMethodClientRequested += OnTextInputMethodClientRequested;
    }

    public bool ShowCaretOnClick
    {
        get => GetValue(ShowCaretOnClickProperty);
        set => SetValue(ShowCaretOnClickProperty, value);
    }

    /// <summary>
    ///     Gets a value indicating whether the terminal is currently using the alternate screen buffer.
    /// </summary>
    public bool IsAlternateBuffer { get; private set; }

    /// <summary>
    ///     Gets or sets the terminal scrollback buffer size in lines.
    /// </summary>
    public int BufferSize
    {
        get => _bufferSize;
        set
        {
            Terminal.Options.Scrollback = value;
            SetAndRaise(BufferSizeProperty, ref _bufferSize, value);
            this.RequestInvalidate();
        }
    }

    /// <summary>
    ///     The absolute line index of the top of the viewport in the buffer.
    ///     0 = top of buffer, higher values = scrolled forward towards current output.
    /// </summary>
    public int ViewportY
    {
        get => Terminal.Buffer.ViewportY;
        set
        {
            int oldValue = Terminal.Buffer.ViewportY;
            Terminal.Buffer.ViewportY = value;

            if (oldValue != Terminal.Buffer.ViewportY)
            {
                RaisePropertyChanged(ViewportYProperty, oldValue, Terminal.Buffer.ViewportY);
                this.RequestInvalidate();
            }
        }
    }

    /// <summary>
    ///     Maximum scroll position (total buffer lines - viewport lines).
    ///     This is the maximum value ViewportY can be.
    /// </summary>
    public int MaxScrollback
    {
        get
        {
            // Simple: total lines in buffer minus how many we can see
            int totalLines = Terminal.Buffer.Length;
            int viewportLines = Terminal.Rows;
            int max = Math.Max(0, totalLines - viewportLines);
            return max;
        }
    }

    public int ViewportLines => Terminal.Rows;

    public XT.Terminal Terminal { get; private set; }

    /// <summary>
    ///     Gets the exit code of the launched PTY process after it has terminated.
    /// </summary>
    public int ExitCode => _ptyConnection?.ExitCode ?? -1;

    /// <summary>
    ///     Gets the operating system process identifier of the launched PTY process.
    /// </summary>
    public int Pid => _ptyConnection!.Pid;

    /// <summary>
    ///     Gets or sets the font family used to render terminal text.
    /// </summary>
    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>
    ///     Gets or sets the font size used to render terminal text.
    /// </summary>
    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>
    ///     Gets or sets the font style used to render terminal text.
    /// </summary>
    public FontStyle FontStyle
    {
        get => GetValue(FontStyleProperty);
        set => SetValue(FontStyleProperty, value);
    }

    /// <summary>
    ///     Gets or sets the font weight used to render terminal text.
    /// </summary>
    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    /// <summary>
    ///     Gets or sets the text decoration locations applied to terminal text.
    /// </summary>
    public TextDecorationLocation? TextDecorations
    {
        get => GetValue(TextDecorationsProperty);
        set => SetValue(TextDecorationsProperty, value);
    }

    /// <summary>
    ///     Gets or sets the font feature collection applied to terminal text.
    /// </summary>
    public FontFeatureCollection FontFeatures
    {
        get => GetValue(FontFeaturesProperty);
        set => SetValue(FontFeaturesProperty, value);
    }

    /// <summary>
    ///     Gets or sets the default foreground brush used for terminal text.
    /// </summary>
    public IBrush Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>
    ///     Gets or sets the terminal background brush.
    /// </summary>
    public IBrush Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
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
    ///     Gets or sets the cursor color used when rendering the terminal caret.
    /// </summary>
    public Color CursorColor
    {
        get => GetValue(CursorColorProperty);
        set => SetValue(CursorColorProperty, value);
    }

    /// <summary>
    ///     Gets or sets the cursor style used by the terminal.
    /// </summary>
    public CursorStyle CursorStyle
    {
        get => GetValue(CursorStyleProperty);
        set => SetValue(CursorStyleProperty, value);
    }

    /// <summary>
    ///     Gets or sets a value indicating whether the terminal cursor should blink.
    /// </summary>
    public bool CursorBlink
    {
        get => GetValue(CursorBlinkProperty);
        set => SetValue(CursorBlinkProperty, value);
    }

    /// <summary>
    ///     Gets or sets the cursor blink rate in milliseconds.
    /// </summary>
    public int CursorBlinkRate
    {
        get => GetValue(CursorBlinkRateProperty);
        set => SetValue(CursorBlinkRateProperty, value);
    }

    /// <summary>
    ///     Gets or sets the terminal emulation options used to configure the inner <see cref="XTerm.Terminal" />.
    /// </summary>
    public TerminalOptions? Options
    {
        get => GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public bool CloseProcessOnDetach
    {
        get => GetValue(CloseProcessOnDetachProperty);
        set => SetValue(CloseProcessOnDetachProperty, value);
    }

    /// <summary>
    ///     Event raised when the PTY process exits.
    /// </summary>
    public event EventHandler<ProcessExitedEventArgs>? ProcessExited;

    /// <summary>
    ///     Event raised when the terminal title changes.
    /// </summary>
    public event EventHandler<TitleChangedEventArgs>? TitleChanged;

    /// <summary>
    ///     Event raised when a window move command is received from the terminal.
    /// </summary>
    public event EventHandler<WindowMovedEventArgs>? WindowMoved;

    /// <summary>
    ///     Event raised when a window resize command is received from the terminal.
    /// </summary>
    public event EventHandler<WindowResizedEventArgs>? WindowResized;

    /// <summary>
    ///     Event raised when a window minimize command is received from the terminal.
    /// </summary>
    public event EventHandler? WindowMinimized;

    /// <summary>
    ///     Event raised when a window maximize command is received from the terminal.
    /// </summary>
    public event EventHandler? WindowMaximized;

    /// <summary>
    ///     Event raised when a window restore command is received from the terminal.
    /// </summary>
    public event EventHandler? WindowRestored;

    /// <summary>
    ///     Event raised when a window raise command is received from the terminal.
    /// </summary>
    public event EventHandler? WindowRaised;

    /// <summary>
    ///     Event raised when a window lower command is received from the terminal.
    /// </summary>
    public event EventHandler? WindowLowered;

    /// <summary>
    ///     Event raised when a window fullscreen command is received from the terminal.
    /// </summary>
    public event EventHandler? WindowFullscreened;

    /// <summary>
    ///     Event raised when the terminal bell is activated.
    /// </summary>
    public event EventHandler? BellRang;

    /// <summary>
    ///     Event raised when window information is requested by the terminal.
    ///     The handler should set the response properties on the event args.
    /// </summary>
    public event EventHandler<WindowInfoRequestedEventArgs>? WindowInfoRequested;

    protected override void OnInitialized()
    {
        // Sync terminal options with styled properties
        TerminalOptions options = Options ?? new TerminalOptions();

        options.CursorStyle = CursorStyle;
        options.CursorBlink = CursorBlink;
        options.CursorBlinkRate = CursorBlinkRate;

        // On Linux, the PTY doesn't convert LF to CRLF (ONLCR is disabled for raw mode),
        // so we need XTerm to handle LF as implicit CRLF
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            options.ConvertEol = true;
        }

        Terminal = new XT.Terminal(options);

        Terminal.DataReceived += OnTerminalDataReceived;
        Terminal.BufferChanged += OnTerminalBufferChanged;
        Terminal.CursorStyleChanged += OnTerminalCursorStyleChanged;
        // window events
        Terminal.TitleChanged += OnTerminalTitleChanged;
        Terminal.WindowMoved += OnTerminalWindowMoved;
        Terminal.WindowResized += OnTerminalWindowResized;
        Terminal.WindowMinimized += OnTerminalWindowMinimized;
        Terminal.WindowMaximized += OnTerminalWindowMaximized;
        Terminal.WindowRestored += OnTerminalWindowRestored;
        Terminal.WindowRaised += OnTerminalWindowRaised;
        Terminal.WindowLowered += OnTerminalWindowLowered;
        Terminal.WindowFullscreened += OnTerminalWindowFullscreened;
        Terminal.BellRang += OnTerminalBellRang;
        Terminal.WindowInfoRequested += OnTerminalWindowInfoRequested;
        Terminal.DirectoryChanged += OnTerminalDirectoryChanged;
        // end window events

        // Setup cursor blink timer
        _cursorBlinkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(CursorBlinkRate)
        };
        _cursorBlinkTimer.Tick += OnCursorBlinkTick;

        // Initialize IME client
        _inputMethodClient = new TerminalInputMethodClient(this);

        RegisterActiveView(this);
    }

    private void OnTerminalDirectoryChanged(object? sender, TerminalEvents.DirectoryChangeEventArgs e)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            string? oldValue = _currentDirectory;
            _currentDirectory = e.Directory;
            RaisePropertyChanged(CurrentDirectoryProperty, oldValue, _currentDirectory);
        });
    }

    public void WaitForExit(int ms)
    {
        _ptyConnection?.WaitForExit(ms);
    }

    public void Kill()
    {
        _ptyConnection?.Kill();
    }

    /// <summary>
    ///     Pastes text from the clipboard into the terminal.
    /// </summary>
    public async Task PasteAsync()
    {
        if (_ptyConnection == null)
        {
            return;
        }

        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
        {
            return;
        }

        string? text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            // Wrap paste in bracketed paste sequences if mode is enabled
            if (Terminal.BracketedPasteMode)
            {
                text = $"\e[200~{text}\e[201~";
            }

            await SendToPtyAsync(text);
        }
    }

    /// <summary>
    ///     Copies selected text to the clipboard.
    /// </summary>
    /// <returns>True if text was copied, false if no selection.</returns>
    public async Task<bool> CopyAsync()
    {
        if (!Terminal.Selection.HasSelection)
        {
            return false;
        }

        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
        {
            return false;
        }

        string text = Terminal.Selection.GetSelectionText();
        if (!string.IsNullOrEmpty(text))
        {
            // Normalize line endings for the current platform
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Ensure Windows gets \r\n line endings
                text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            }

            await clipboard.SetTextAsync(text);
            return true;
        }

        return false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CursorStyleProperty)
        {
            Terminal.Options.CursorStyle = (CursorStyle)change.NewValue!;
        }
        else if (change.Property == CursorBlinkProperty)
        {
            bool blink = (bool)change.NewValue!;
            Terminal.Options.CursorBlink = blink;

            if (blink && IsFocused)
            {
                _cursorBlinkTimer.Start();
            }
            else
            {
                _cursorBlinkTimer.Stop();
                _cursorBlinkOn = true; // Reset to visible when blinking stops
            }
        }
        else if (change.Property == CursorBlinkRateProperty)
        {
            int rate = (int)change.NewValue!;
            Terminal.Options.CursorBlinkRate = rate;
            _cursorBlinkTimer.Interval = TimeSpan.FromMilliseconds(rate > 0 ? rate : 530);
        }
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_ptyConnection == null && !string.IsNullOrEmpty(Process))
            {
                await LaunchProcess();
            }

            // Start cursor blinking if enabled
            if (CursorBlink)
            {
                _cursorBlinkTimer.Start();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TerminalView] Error launching process: {ex}");
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _cursorBlinkTimer.Stop();
        _isSelecting = false;
        _pendingSelectionStart = null;
    }

    /// <summary>
    ///     Call before removing this view from one visual tree and adding it to another.
    ///     Prevents <see cref="OnDetachedFromLogicalTree" /> from killing the PTY process.
    ///     Must be paired with <see cref="EndReparent" /> once re-attached.
    /// </summary>
    public void BeginReparent()
    {
        _suppressCleanupOnDetach = true;
    }

    /// <summary>
    ///     Call after the view has been re-attached to a new visual tree to restore
    ///     normal cleanup behaviour and ensure render handlers are wired up.
    /// </summary>
    public void EndReparent()
    {
        _suppressCleanupOnDetach = false;
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromLogicalTree(e);
        UnregisterActiveView(this);

        Terminal.DataReceived -= OnTerminalDataReceived;
        Terminal.BufferChanged -= OnTerminalBufferChanged;
        Terminal.CursorStyleChanged -= OnTerminalCursorStyleChanged;
        Terminal.TitleChanged -= OnTerminalTitleChanged;
        Terminal.WindowMoved -= OnTerminalWindowMoved;
        Terminal.WindowResized -= OnTerminalWindowResized;
        Terminal.WindowMinimized -= OnTerminalWindowMinimized;
        Terminal.WindowMaximized -= OnTerminalWindowMaximized;
        Terminal.WindowRestored -= OnTerminalWindowRestored;
        Terminal.WindowRaised -= OnTerminalWindowRaised;
        Terminal.WindowLowered -= OnTerminalWindowLowered;
        Terminal.WindowFullscreened -= OnTerminalWindowFullscreened;
        Terminal.BellRang -= OnTerminalBellRang;
        Terminal.DirectoryChanged -= OnTerminalDirectoryChanged;
        Terminal.WindowInfoRequested -= OnTerminalWindowInfoRequested;

        if (CloseProcessOnDetach && !_suppressCleanupOnDetach)
        {
            CleanupProcess();
        }
    }

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);

        // _terminal is null during initial attachment (OnInitialized hasn't fired yet).
        // Only re-subscribe when re-parenting after a prior detach.
        if (Terminal == null)
        {
            return;
        }

        // Re-subscribe terminal events that were unsubscribed on detach.
        // Use -= before += to avoid double-subscription.
        Terminal.DataReceived -= OnTerminalDataReceived;
        Terminal.BufferChanged -= OnTerminalBufferChanged;
        Terminal.CursorStyleChanged -= OnTerminalCursorStyleChanged;
        Terminal.TitleChanged -= OnTerminalTitleChanged;
        Terminal.WindowMoved -= OnTerminalWindowMoved;
        Terminal.WindowResized -= OnTerminalWindowResized;
        Terminal.WindowMinimized -= OnTerminalWindowMinimized;
        Terminal.WindowMaximized -= OnTerminalWindowMaximized;
        Terminal.WindowRestored -= OnTerminalWindowRestored;
        Terminal.WindowRaised -= OnTerminalWindowRaised;
        Terminal.WindowLowered -= OnTerminalWindowLowered;
        Terminal.WindowFullscreened -= OnTerminalWindowFullscreened;
        Terminal.BellRang -= OnTerminalBellRang;
        Terminal.DirectoryChanged -= OnTerminalDirectoryChanged;
        Terminal.WindowInfoRequested -= OnTerminalWindowInfoRequested;

        Terminal.DataReceived += OnTerminalDataReceived;
        Terminal.BufferChanged += OnTerminalBufferChanged;
        Terminal.CursorStyleChanged += OnTerminalCursorStyleChanged;
        Terminal.TitleChanged += OnTerminalTitleChanged;
        Terminal.WindowMoved += OnTerminalWindowMoved;
        Terminal.WindowResized += OnTerminalWindowResized;
        Terminal.WindowMinimized += OnTerminalWindowMinimized;
        Terminal.WindowMaximized += OnTerminalWindowMaximized;
        Terminal.WindowRestored += OnTerminalWindowRestored;
        Terminal.WindowRaised += OnTerminalWindowRaised;
        Terminal.WindowLowered += OnTerminalWindowLowered;
        Terminal.WindowFullscreened += OnTerminalWindowFullscreened;
        Terminal.BellRang += OnTerminalBellRang;
        Terminal.DirectoryChanged += OnTerminalDirectoryChanged;
        Terminal.WindowInfoRequested += OnTerminalWindowInfoRequested;
    }

    private void OnCursorBlinkTick(object? sender, EventArgs e)
    {
        if (CursorBlink && IsFocused)
        {
            _cursorBlinkOn = !_cursorBlinkOn;
            for (int y = 0; y < Terminal.Rows; y++)
            {
                BufferLine? line = Terminal.Buffer.GetLine(y);
                if (line != null && line.Any(cell => cell.Attributes.IsBlink()))
                {
                    line.Cache = null;
                }
            }

            this.RequestInvalidate();
        }
    }

    // True when the key is a modifier pressed on its own (no associated character),
    // e.g. the ⌘/Ctrl/Shift/Alt keys. Used so a bare modifier press doesn't clear
    // an active selection before the rest of a copy shortcut is typed.
    private static bool IsModifierKey(Key key)
    {
        return key switch
        {
            Key.LeftShift or Key.RightShift or
                Key.LeftCtrl or Key.RightCtrl or
                Key.LeftAlt or Key.RightAlt or
                Key.LWin or Key.RWin => true,
            _ => false
        };
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        try
        {
            // Only process input if this terminal has focus
            if (!IsFocused)
            {
                base.OnKeyDown(e);
                return;
            }

            // Capture the connection reference locally
            IPtyConnection? ptyConnection = _ptyConnection;
            if (ptyConnection == null)
            {
                Debug.WriteLine("[TerminalView] No PTY connection");
                base.OnKeyDown(e);
                return;
            }

            // When the process has exited, stop eating keyboard input so that Avalonia's
            // normal focus navigation (Tab/Shift+Tab etc.) works again.  We still handle
            // the copy shortcut so the user can copy terminal output after a run.
            if (_processExitHandled != 0)
            {
                bool isCopy = e.Key == Key.C &&
                              (e.KeyModifiers == KeyModifiers.Control ||
                               e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) ||
                               (IsMacOs && e.KeyModifiers == KeyModifiers.Meta));
                if (isCopy && Terminal.Selection.HasSelection)
                {
                    e.Handled = true;
                    await CopyAsync();
                    Terminal.Selection.ClearSelection();
                    this.RequestInvalidate();
                }
                else
                {
                    base.OnKeyDown(e);
                }

                return;
            }

            try
            {
                // macOS clipboard shortcuts use the Command (Meta) key. These don't collide
                // with terminal control codes (SIGINT is Ctrl+C, not Cmd+C), so we can handle
                // them directly here. On Windows/Linux this block is skipped and the
                // Ctrl / Ctrl+Shift shortcuts below are used instead.
                if (IsMacOs && e.KeyModifiers == KeyModifiers.Meta)
                {
                    // Cmd+C - copy the selection (no-op when nothing is selected, matching macOS)
                    if (e.Key == Key.C)
                    {
                        e.Handled = true;
                        if (Terminal.Selection.HasSelection)
                        {
                            await CopyAsync();
                            Terminal.Selection.ClearSelection();
                            this.RequestInvalidate();
                        }

                        return;
                    }

                    // Cmd+V - paste from the clipboard
                    if (e.Key == Key.V)
                    {
                        e.Handled = true;
                        await PasteAsync();
                        return;
                    }
                }

                // Handle Ctrl+C - copy if there's a selection, otherwise send SIGINT
                if (e is { Key: Key.C, KeyModifiers: KeyModifiers.Control })
                {
                    if (Terminal.Selection.HasSelection)
                    {
                        e.Handled = true;
                        await CopyAsync();
                        Terminal.Selection.ClearSelection();
                        this.RequestInvalidate();
                        return;
                    }
                    // No selection - fall through to send Ctrl+C (SIGINT) to the process
                }

                // Handle Ctrl+Shift+C for copy (always copies, doesn't send SIGINT)
                if (e is { Key: Key.C, KeyModifiers: (KeyModifiers.Control | KeyModifiers.Shift) })
                {
                    if (Terminal.Selection.HasSelection)
                    {
                        e.Handled = true;
                        await CopyAsync();
                        Terminal.Selection.ClearSelection();
                        this.RequestInvalidate();
                        return;
                    }
                }

                // Clear selection for any other keystroke - but ignore bare modifier
                // presses. Pressing ⌘/Ctrl/Shift on its own fires a KeyDown before the
                // shortcut's letter arrives; clearing here would lose the selection
                // before Cmd+C / Ctrl+Shift+C could copy it.
                if (Terminal.Selection.HasSelection && !IsModifierKey(e.Key))
                {
                    Terminal.Selection.ClearSelection();
                    this.RequestInvalidate();
                }

                // Handle Ctrl+Shift+V for paste (standard terminal shortcut)
                // Ctrl+V is NOT intercepted - it gets passed to the application
                // (some apps use Ctrl+V for literal character input mode)
                if (e is { Key: Key.V, KeyModifiers: (KeyModifiers.Control | KeyModifiers.Shift) })
                {
                    e.Handled = true;
                    await PasteAsync();
                    return;
                }

                XT.Input.KeyModifiers modifiers = ConvertAvaloniaModifiers(e.KeyModifiers);

                // Windows ConPTY limitation: There is no VT sequence for plain ESCAPE key.
                // When ENABLE_VIRTUAL_TERMINAL_INPUT is enabled (by cmd.exe), the only way
                // to send ESCAPE is via Win32 INPUT_RECORD format. Always use Win32 for ESC on Windows.
                bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                bool isEscapeKey = e.Key == Key.Escape;
                bool useWin32Format = Terminal.Win32InputMode || (isWindows && isEscapeKey);

                if (useWin32Format)
                {
                    string sequence = GenerateWin32InputSequence(e, true);
                    if (!string.IsNullOrEmpty(sequence))
                    {
                        e.Handled = true;

                        await SendToPtyAsync(sequence).ConfigureAwait(false);
                        return;
                    }
                    // If we couldn't generate a Win32 sequence, fall through to normal handling
                    // This can happen for keys that don't have a virtual key mapping
                }

                // Convert Avalonia key to XTerm key
                XT.Input.Key? xtermKey = ConvertAvaloniaKeyToXTermKey(e.Key);

                // Special keys (arrows, function keys, Tab, etc.) - always handle in KeyDown
                if (xtermKey != null)
                {
                    string sequence = Terminal.GenerateKeyInput(xtermKey.Value, modifiers);
                    if (!string.IsNullOrEmpty(sequence))
                    {
                        e.Handled = true;
                        await SendToPtyAsync(sequence).ConfigureAwait(false);
                    }

                    return;
                }

                // Ctrl/Alt + character combinations (these don't generate TextInput events)
                if ((modifiers & (XT.Input.KeyModifiers.Control | XT.Input.KeyModifiers.Alt)) != 0)
                {
                    if (TryGetPrintableChar(e, out char keyChar))
                    {
                        string sequence = Terminal.GenerateCharInput(keyChar, modifiers);
                        if (!string.IsNullOrEmpty(sequence))
                        {
                            e.Handled = true;
                            await SendToPtyAsync(sequence).ConfigureAwait(false);
                        }
                    }

                    return;
                }

                // Try to get a printable character - first from KeySymbol, then from key mapping
                // This is critical for Consolonia where KeySymbol may be empty
                if (TryGetPrintableChar(e, out char printableChar))
                {
                    e.Handled = true;
                    await SendToPtyAsync(printableChar.ToString()).ConfigureAwait(false);
                }

                // If we couldn't handle it, let TextInput try (for desktop Avalonia)
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_instanceId}] Error handling key input: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{_instanceId}] Unexpected error in OnKeyDown: {ex.Message}");
        }
    }

    protected override async void OnKeyUp(KeyEventArgs e)
    {
        try
        {
            // Only process input if this terminal has focus
            if (!IsFocused)
            {
                base.OnKeyUp(e);
                return;
            }

            // Capture the connection reference locally
            IPtyConnection? ptyConnection = _ptyConnection;
            if (ptyConnection == null || _processExitHandled != 0)
            {
                base.OnKeyUp(e);
                return;
            }

            try
            {
                // Windows ConPTY limitation: Always send ESCAPE key in Win32 format
                bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                bool isEscapeKey = e.Key == Key.Escape;
                bool useWin32Format = Terminal.Win32InputMode || (isWindows && isEscapeKey);

                if (useWin32Format)
                {
                    string sequence = GenerateWin32InputSequence(e, false);
                    if (!string.IsNullOrEmpty(sequence))
                    {
                        await SendToPtyAsync(sequence).ConfigureAwait(false);
                        e.Handled = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_instanceId}] Error handling key up: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{_instanceId}] Unexpected error in OnKeyUp: {ex.Message}");
        }
    }

    protected override async void OnTextInput(TextInputEventArgs e)
    {
        try
        {
            // Only process input if this terminal has focus
            if (!IsFocused)
            {
                Debug.WriteLine("[TerminalView] OnTextInput: Not focused, passing to base");
                base.OnTextInput(e);
                return;
            }

            // Capture the connection reference locally
            IPtyConnection? ptyConnection = _ptyConnection;
            if (ptyConnection == null || string.IsNullOrEmpty(e.Text) || _processExitHandled != 0)
            {
                Debug.WriteLine("[TerminalView] OnTextInput: No PTY or empty text");
                base.OnTextInput(e);
                return;
            }

            // In Win32 Input Mode, text input is handled via KeyDown/KeyUp events
            if (Terminal.Win32InputMode)
            {
                Debug.WriteLine("[TerminalView] OnTextInput: Win32 input mode, skipping");
                return;
            }

            // Clear selection when text is being input
            if (Terminal.Selection.HasSelection)
            {
                Terminal.Selection.ClearSelection();
                this.RequestInvalidate();
            }

            try
            {
                Debug.WriteLine($"[TerminalView] OnTextInput: Sending '{e.Text}' to PTY");
                await SendToPtyAsync(e.Text).ConfigureAwait(false);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{_instanceId}] Error handling text input: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{_instanceId}] Unexpected error in OnTextInput: {ex.Message}");
        }
    }

    protected override async void OnPointerPressed(PointerPressedEventArgs e)
    {
        try
        {
            base.OnPointerPressed(e);

            // Request focus when clicked
            Focus();

            try
            {
                Point point = e.GetPosition(this);
                int col = (int)(point.X / _charWidth);
                int row = (int)(point.Y / _charHeight);

                // Check if we should handle selection (app doesn't want mouse, or Shift override)
                if (ShouldHandleSelection(e.KeyModifiers))
                {
                    PointerPointProperties props = e.GetCurrentPoint(this).Properties;

                    // Right-click: copy if selection exists, otherwise paste
                    if (props.IsRightButtonPressed)
                    {
                        if (Terminal.Selection.HasSelection)
                        {
                            await CopyAsync();
                            Terminal.Selection.ClearSelection();
                            this.RequestInvalidate();
                        }
                        else
                        {
                            await PasteAsync();
                        }

                        e.Handled = true;
                        return;
                    }

                    // Left-click clears existing selection before starting new one
                    if (props.IsLeftButtonPressed && Terminal.Selection.HasSelection)
                    {
                        Terminal.Selection.ClearSelection();
                        this.RequestInvalidate();
                    }

                    // Determine selection mode based on click count
                    int clickCount = e.ClickCount;
                    SelectionMode mode = clickCount switch
                    {
                        2 => SelectionMode.Word,
                        3 => SelectionMode.Line,
                        _ => SelectionMode.Normal
                    };

                    if (mode == SelectionMode.Normal && !ShowCaretOnClick)
                    {
                        // Defer single-click selection until the pointer actually moves;
                        // this avoids showing a single-cell caret on every click.
                        _pendingSelectionStart = (col, row);
                        _isSelecting = true;
                    }
                    else
                    {
                        // Word / line select, or ShowCaretOnClick=true — start immediately.
                        int viewportRow = row;
                        Terminal.Selection.StartSelection(col, viewportRow, mode);
                        _isSelecting = true;
                        _pendingSelectionStart = null;
                        this.RequestInvalidate();
                    }

                    e.Handled = true;
                    return;
                }

                // Forward mouse event to application
                if (_ptyConnection == null)
                {
                    return;
                }

                XT.Input.MouseButton button = ConvertPointerButton(e.GetCurrentPoint(this).Properties);
                XT.Input.KeyModifiers modifiers = ConvertAvaloniaModifiers(e.KeyModifiers);

                string sequence = Terminal.GenerateMouseEvent(button, col, row, MouseEventType.Down, modifiers);
                if (!string.IsNullOrEmpty(sequence))
                {
                    await SendToPtyAsync(sequence).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling mouse press: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unexpected error in OnPointerPressed: {ex.Message}");
        }
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        try
        {
            base.OnPointerReleased(e);

            try
            {
                // If we were selecting, end selection
                if (_isSelecting)
                {
                    if (_pendingSelectionStart.HasValue)
                    {
                        // Pointer was never moved after the click — no visible selection was started,
                        // so just clear the pending state without leaving any caret.
                        _pendingSelectionStart = null;
                    }
                    else
                    {
                        Terminal.Selection.EndSelection();
                    }

                    _isSelecting = false;
                    e.Handled = true;
                    return;
                }

                // Forward mouse event to application
                if (_ptyConnection == null)
                {
                    return;
                }

                Point point = e.GetPosition(this);
                int col = (int)(point.X / _charWidth);
                int row = (int)(point.Y / _charHeight);

                XT.Input.MouseButton button =
                    ConvertPointerButton(e.GetCurrentPoint(this).Properties, e.InitialPressMouseButton);
                XT.Input.KeyModifiers modifiers = ConvertAvaloniaModifiers(e.KeyModifiers);

                string sequence = Terminal.GenerateMouseEvent(button, col, row, MouseEventType.Up, modifiers);
                if (!string.IsNullOrEmpty(sequence))
                {
                    await SendToPtyAsync(sequence).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling mouse release: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unexpected error in OnPointerReleased: {ex.Message}");
        }
    }

    protected override async void OnPointerMoved(PointerEventArgs e)
    {
        try
        {
            base.OnPointerMoved(e);

            try
            {
                Point point = e.GetPosition(this);
                int col = (int)(point.X / _charWidth);
                int row = (int)(point.Y / _charHeight);

                // If we're selecting, update the selection
                if (_isSelecting)
                {
                    int viewportRow = row;
                    if (_pendingSelectionStart.HasValue)
                    {
                        // First movement after a single click — now actually start the selection.
                        Terminal.Selection.StartSelection(_pendingSelectionStart.Value.Col,
                            _pendingSelectionStart.Value.Row);
                        _pendingSelectionStart = null;
                    }

                    Terminal.Selection.UpdateSelection(col, viewportRow);
                    this.RequestInvalidate();
                    e.Handled = true;
                    return;
                }

                // Forward mouse event to application
                if (_ptyConnection == null)
                {
                    return;
                }

                PointerPointProperties props = e.GetCurrentPoint(this).Properties;
                XT.Input.KeyModifiers modifiers = ConvertAvaloniaModifiers(e.KeyModifiers);
                XT.Input.MouseButton button = ConvertPointerButton(props);
                MouseEventType eventType =
                    props.IsLeftButtonPressed || props.IsMiddleButtonPressed || props.IsRightButtonPressed
                        ? MouseEventType.Drag
                        : MouseEventType.Move;

                string sequence = Terminal.GenerateMouseEvent(button, col, row, eventType, modifiers);
                if (!string.IsNullOrEmpty(sequence))
                {
                    await SendToPtyAsync(sequence).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling mouse move: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unexpected error in OnPointerMoved: {ex.Message}");
        }
    }

    protected override async void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        try
        {
            base.OnPointerWheelChanged(e);

            // Number of lines to scroll per wheel notch
            const int scrollLines = 3;

            // Delta.Y is positive when scrolling up (towards user), negative when scrolling down
            double delta = e.Delta.Y;

            if (_ptyConnection != null && Terminal.MouseTrackingMode != MouseTrackingMode.None)
            {
                Point point = e.GetPosition(this);
                int col = (int)(point.X / _charWidth);
                int row = (int)(point.Y / _charHeight);
                XT.Input.KeyModifiers modifiers = ConvertAvaloniaModifiers(e.KeyModifiers);

                XT.Input.MouseButton button =
                    delta > 0 ? XT.Input.MouseButton.WheelUp : XT.Input.MouseButton.WheelDown;
                MouseEventType eventType = delta > 0 ? MouseEventType.WheelUp : MouseEventType.WheelDown;

                string sequence = Terminal.GenerateMouseEvent(button, col, row, eventType, modifiers);
                if (!string.IsNullOrEmpty(sequence))
                {
                    await SendToPtyAsync(sequence).ConfigureAwait(false);
                    e.Handled = true;
                    return;
                }
            }

            if (delta != 0)
            {
                // Scroll up (negative delta to ViewportY) when wheel scrolls up (positive delta)
                // Scroll down (positive delta to ViewportY) when wheel scrolls down (negative delta)
                int linesToScroll = (int)(-delta * scrollLines);

                // Calculate new viewport position
                int newViewportY = Math.Clamp(
                    ViewportY + linesToScroll,
                    0,
                    MaxScrollback);

                if (newViewportY != ViewportY)
                {
                    ViewportY = newViewportY;
                }

                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unexpected error in OnPointerWheelChanged: {ex.Message}");
        }
    }

    protected override async void OnGotFocus(FocusChangedEventArgs e)
    {
        try
        {
            base.OnGotFocus(e);

            Debug.WriteLine($"[TerminalView] OnGotFocus: Source={e.Source?.GetType().Name}");

            // Reset blink state to visible when focused
            _cursorBlinkOn = true;
            if (CursorBlink)
            {
                _cursorBlinkTimer.Start();
            }

            if (_ptyConnection != null && Terminal.SendFocusEvents)
            {
                string sequence = Terminal.GenerateFocusEvent(true);
                if (!string.IsNullOrEmpty(sequence))
                {
                    await SendToPtyAsync(sequence);
                }
            }

            this.RequestInvalidate();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TerminalView] Error in OnGotFocus: {ex.Message}");
        }
    }

    protected override async void OnLostFocus(FocusChangedEventArgs e)
    {
        try
        {
            base.OnLostFocus(e);

            Debug.WriteLine("[TerminalView] OnLostFocus");

            // Stop blinking when not focused, but keep cursor visible (hollow block)
            _cursorBlinkTimer.Stop();
            _cursorBlinkOn = true;

            // Clear any preedit text when focus is lost
            _inputMethodClient?.ClearPreeditText();

            if (_ptyConnection != null && Terminal.SendFocusEvents)
            {
                string sequence = Terminal.GenerateFocusEvent(false);
                if (!string.IsNullOrEmpty(sequence))
                {
                    await SendToPtyAsync(sequence);
                }
            }

            this.RequestInvalidate();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TerminalView] Error in OnLostFocus: {ex.Message}");
        }
    }

    private void OnTextInputMethodClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
    {
        e.Client = _inputMethodClient;
    }

    private void OnTerminalBufferChanged(object? sender, TerminalEvents.BufferChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            bool oldValue = IsAlternateBuffer;
            IsAlternateBuffer = e.Buffer == BufferType.Alternate;

            if (oldValue != IsAlternateBuffer)
            {
                RaisePropertyChanged(IsAlternateBufferProperty, oldValue, IsAlternateBuffer);
            }

            RaisePropertyChanged(MaxScrollbackProperty, 0, MaxScrollback);
            RaisePropertyChanged(ViewportLinesProperty, 0, ViewportLines);
            RaisePropertyChanged(ViewportYProperty, 0, ViewportY);
            this.RequestInvalidate();
        });
    }

    private void OnTerminalCursorStyleChanged(object? sender, TerminalEvents.CursorStyleChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!Equals(CursorStyle, e.Style))
            {
                SetValue(CursorStyleProperty, e.Style);
            }

            if (!Equals(CursorBlink, e.Blink))
            {
                SetValue(CursorBlinkProperty, e.Blink);
            }

            this.RequestInvalidate();
        });
    }

    private void OnTerminalTitleChanged(object? sender, TerminalEvents.TitleChangeEventArgs e)
    {
        if (e.Title == null)
        {
            return;
        }

        if (e.Title.StartsWith("__CLEAR_SCROLLBACK__"))
        {
            Dispatcher.UIThread.Post(() =>
            {
                lock (_terminalLock)
                {
                    Terminal.ClearScrollback();
                    ClearCache();
                }

                RaisePropertyChanged(MaxScrollbackProperty, 0, MaxScrollback);
                RaisePropertyChanged(ViewportYProperty, 0, ViewportY);
                this.RequestInvalidate();
            });

            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            TitleChangedEventArgs args = new(e.Title)
            {
                RoutedEvent = TitleChangedEvent
            };

            RaiseEvent(args);
            TitleChanged?.Invoke(this, args);
        });
    }

    private void OnTerminalWindowMoved(object? sender, TerminalEvents.WindowMovedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            WindowMovedEventArgs args = new(e.X, e.Y)
            {
                RoutedEvent = WindowMovedEvent
            };

            RaiseEvent(args);
            WindowMoved?.Invoke(this, args);
        });
    }

    private void OnTerminalWindowResized(object? sender, TerminalEvents.WindowResizedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            WindowResizedEventArgs args = new(e.Width, e.Height)
            {
                RoutedEvent = WindowResizedEvent
            };

            RaiseEvent(args);
            WindowResized?.Invoke(this, args);
        });
    }

    private void OnTerminalWindowMinimized(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RoutedEventArgs args = new(WindowMinimizedEvent);
            RaiseEvent(args);
            WindowMinimized?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnTerminalWindowMaximized(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RoutedEventArgs args = new(WindowMaximizedEvent);
            RaiseEvent(args);
            WindowMaximized?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnTerminalWindowRestored(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RoutedEventArgs args = new(WindowRestoredEvent);
            RaiseEvent(args);
            WindowRestored?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnTerminalWindowRaised(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RoutedEventArgs args = new(WindowRaisedEvent);
            RaiseEvent(args);
            WindowRaised?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnTerminalWindowLowered(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RoutedEventArgs args = new(WindowLoweredEvent);
            RaiseEvent(args);
            WindowLowered?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnTerminalWindowFullscreened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RoutedEventArgs args = new(WindowFullscreenedEvent);
            RaiseEvent(args);
            WindowFullscreened?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnTerminalBellRang(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RoutedEventArgs args = new(BellRangEvent);
            RaiseEvent(args);
            BellRang?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnTerminalWindowInfoRequested(object? sender, TerminalEvents.WindowInfoRequestedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Raise routed event so any parent can handle it without custom plumbing.
            WindowInfoRequestedEventArgs args = new(e.Request)
            {
                RoutedEvent = WindowInfoRequestedEvent
            };

            RaiseEvent(args);

            // Keep CLR event for back-compat.
            WindowInfoRequested?.Invoke(this, args);

            // Copy response data back to the terminal's event args
            if (args.Handled)
            {
                e.Handled = true;
                e.IsIconified = args.IsIconified;
                e.X = args.X;
                e.Y = args.Y;
                e.WidthPixels = args.WidthPixels;
                e.HeightPixels = args.HeightPixels;
                e.CellWidth = args.CellWidth;
                e.CellHeight = args.CellHeight;
                e.Title = args.Title;
            }
        });
    }

    private async void OnTerminalDataReceived(object? sender, TerminalEvents.DataEventArgs e)
    {
        try
        {
            // Terminal wants to send data (typically in response to device status queries, etc.)
            await SendToPtyAsync(e.Data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{_instanceId}] Error sending data to PTY: {ex.Message}");
        }
    }

    private async Task SendToPtyAsync(string data, CancellationToken ct = default)
    {
        // Capture the connection reference locally to avoid any potential race conditions
        IPtyConnection? ptyConnection = _ptyConnection;
        if (ptyConnection == null || string.IsNullOrEmpty(data))
        {
            return;
        }

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] bytes = Utf8NoBom.GetBytes(data);
            await ptyConnection.WriterStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            await ptyConnection.WriterStream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{_instanceId}] Error writing to PTY: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private XT.Input.Key? ConvertAvaloniaKeyToXTermKey(Key key)
    {
        return key switch
        {
            Key.Enter => XT.Input.Key.Enter,
            Key.Back => XT.Input.Key.Backspace,
            Key.Tab => XT.Input.Key.Tab,
            Key.Escape => XT.Input.Key.Escape,
            Key.Up => XT.Input.Key.UpArrow,
            Key.Down => XT.Input.Key.DownArrow,
            Key.Left => XT.Input.Key.LeftArrow,
            Key.Right => XT.Input.Key.RightArrow,
            Key.Home => XT.Input.Key.Home,
            Key.End => XT.Input.Key.End,
            Key.PageUp => XT.Input.Key.PageUp,
            Key.PageDown => XT.Input.Key.PageDown,
            Key.Insert => XT.Input.Key.Insert,
            Key.Delete => XT.Input.Key.Delete,
            Key.F1 => XT.Input.Key.F1,
            Key.F2 => XT.Input.Key.F2,
            Key.F3 => XT.Input.Key.F3,
            Key.F4 => XT.Input.Key.F4,
            Key.F5 => XT.Input.Key.F5,
            Key.F6 => XT.Input.Key.F6,
            Key.F7 => XT.Input.Key.F7,
            Key.F8 => XT.Input.Key.F8,
            Key.F9 => XT.Input.Key.F9,
            Key.F10 => XT.Input.Key.F10,
            Key.F11 => XT.Input.Key.F11,
            Key.F12 => XT.Input.Key.F12,
            _ => null
        };
    }

    private XT.Input.KeyModifiers ConvertAvaloniaModifiers(KeyModifiers modifiers)
    {
        XT.Input.KeyModifiers result = XT.Input.KeyModifiers.None;

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            result |= XT.Input.KeyModifiers.Shift;
        }

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            result |= XT.Input.KeyModifiers.Control;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            result |= XT.Input.KeyModifiers.Alt;
        }

        return result;
    }

    private XT.Input.MouseButton ConvertPointerButton(PointerPointProperties props,
        MouseButton? releasedButton = null)
    {
        if (props.IsLeftButtonPressed)
        {
            return XT.Input.MouseButton.Left;
        }

        if (props.IsMiddleButtonPressed)
        {
            return XT.Input.MouseButton.Middle;
        }

        if (props.IsRightButtonPressed)
        {
            return XT.Input.MouseButton.Right;
        }

        if (releasedButton.HasValue)
        {
            return releasedButton.Value switch
            {
                MouseButton.Left => XT.Input.MouseButton.Left,
                MouseButton.Middle => XT.Input.MouseButton.Middle,
                MouseButton.Right => XT.Input.MouseButton.Right,
                _ => XT.Input.MouseButton.None
            };
        }

        return XT.Input.MouseButton.None;
    }

    /// <summary>
    ///     Determines if the terminal should handle text selection vs forwarding mouse to app.
    ///     Selection is handled when: (1) app hasn't captured mouse, OR (2) Shift is held (override).
    /// </summary>
    private bool ShouldHandleSelection(KeyModifiers modifiers)
    {
        bool appWantsMouse = Terminal.MouseTrackingMode != MouseTrackingMode.None;
        bool shiftHeld = modifiers.HasFlag(KeyModifiers.Shift);

        // Handle selection if app doesn't want mouse, OR if Shift override is active
        return !appWantsMouse || shiftHeld;
    }

    private bool TryGetPrintableChar(KeyEventArgs e, out char character)
    {
        // Prefer the symbol provided by Avalonia (already respects layout)
        if (!string.IsNullOrEmpty(e.KeySymbol) && e.KeySymbol.Length == 1 && !char.IsControl(e.KeySymbol[0]))
        {
            character = e.KeySymbol[0];
            return true;
        }

        // Fallback mapping for cases where KeySymbol is empty (e.g., Consolonia, or Alt+<char> on some platforms)
        bool result = TryMapKeyToChar(e.Key, e.KeyModifiers, out character);
        return result;
    }

    private bool TryMapKeyToChar(Key key, KeyModifiers modifiers, out char character)
    {
        character = '\0';
        bool hasShift = modifiers.HasFlag(KeyModifiers.Shift);

        // Letters A-Z
        if (key is >= Key.A and <= Key.Z)
        {
            int offset = key - Key.A;
            character = (char)((hasShift ? 'A' : 'a') + offset);
            return true;
        }

        // Numbers 0-9 (with shift symbols for US keyboard)
        if (key is >= Key.D0 and <= Key.D9)
        {
            if (hasShift)
            {
                // Shift + number = symbol (US keyboard layout)
                character = key switch
                {
                    Key.D1 => '!',
                    Key.D2 => '@',
                    Key.D3 => '#',
                    Key.D4 => '$',
                    Key.D5 => '%',
                    Key.D6 => '^',
                    Key.D7 => '&',
                    Key.D8 => '*',
                    Key.D9 => '(',
                    Key.D0 => ')',
                    // ReSharper disable once UnreachableSwitchArmDueToIntegerAnalysis
                    _ => '\0'
                };
            }
            else
            {
                int offset = key - Key.D0;
                character = (char)('0' + offset);
            }

            return character != 0;
        }

        // Numpad numbers
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            int offset = key - Key.NumPad0;
            character = (char)('0' + offset);
            return true;
        }

        // Common punctuation and OEM keys (US keyboard layout)
        character = key switch
        {
            Key.Space => ' ',
            Key.OemPeriod => hasShift ? '>' : '.',
            Key.OemComma => hasShift ? '<' : ',',
            Key.OemMinus => hasShift ? '_' : '-',
            Key.OemPlus => hasShift ? '+' : '=',
            Key.OemSemicolon => hasShift ? ':' : ';',
            Key.OemQuotes => hasShift ? '"' : '\'',
            Key.OemTilde => hasShift ? '~' : '`',
            Key.OemOpenBrackets => hasShift ? '{' : '[',
            Key.OemCloseBrackets => hasShift ? '}' : ']',
            Key.OemPipe => hasShift ? '|' : '\\',
            Key.OemBackslash => hasShift ? '|' : '\\',
            Key.OemQuestion => hasShift ? '?' : '/',
            Key.Multiply => '*',
            Key.Add => '+',
            Key.Subtract => '-',
            Key.Divide => '/',
            Key.Decimal => '.',
            _ => '\0'
        };

        return character != 0;
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
        CleanupProcess();

        lock (_terminalLock)
        {
            Terminal.Write("\e\x63");

            int currentScrollback = Terminal.Options.Scrollback;
            Terminal.Options.Scrollback = 0;
            Terminal.Options.Scrollback = currentScrollback;

            ClearCache();
        }

        this.RequestInvalidate();

        try
        {
            _processCts = new CancellationTokenSource();
            Interlocked.Exchange(ref _processExitHandled, 0); // Reset flag for new process

            // Determine the process to launch based on OS if not explicitly set
            string processToLaunch = Process;
            if (string.IsNullOrEmpty(processToLaunch))
            {
                processToLaunch = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "bash";
            }

            SetAndRaise(CurrentDirectoryProperty, ref _currentDirectory,
                StartingDirectory ?? Environment.CurrentDirectory);

            PtyOptions options = new()
            {
                Name = processToLaunch,
                Cols = Terminal.Cols,
                Rows = Terminal.Rows,
                Cwd = _currentDirectory ?? Environment.CurrentDirectory,
                App = processToLaunch
            };


            // Add arguments if provided
            if (Args is { Count: > 0 })
            {
                options.CommandLine = Args.ToArray();
            }

            _ptyConnection = await PtyProvider.SpawnAsync(options, _processCts.Token);

            // Subscribe to process exit event for reliable exit detection
            _ptyConnection.ProcessExited += OnPtyProcessExited;

            // Start reading from the PTY connection
            _ = Task.Run(async () => await ReadPtyOutputAsync(_processCts.Token), _processCts.Token);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Terminal.WriteLine($"Error launching process: {ex.Message}\n");
            });
        }
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
    public async Task LaunchProcess(string? startingDirectory, string process, params string[] args)
    {
        StartingDirectory = startingDirectory;
        Process = process;
        Args = args;
        await LaunchProcess();
    }

    private async Task ReadPtyOutputAsync(CancellationToken cancellationToken)
    {
        try
        {
            byte[] buffer = new byte[0x40000];
            while (!cancellationToken.IsCancellationRequested && _ptyConnection != null)
            {
                int bytesRead =
                    await _ptyConnection.ReaderStream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    // Process has exited — fallback in case OnPtyProcessExited didn't fire first.
                    if (Interlocked.Exchange(ref _processExitHandled, 1) == 0)
                    {
                        int exitCode = _ptyConnection?.ExitCode ?? 0;

                        lock (_terminalLock)
                        {
                            Terminal.WriteLine($"\nProcess exited with code: {exitCode}\n");
                            Terminal.Buffer.ScrollToBottom();
                        }

                        this.RequestInvalidate();

                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            ProcessExited?.Invoke(this, new ProcessExitedEventArgs(exitCode));
                        });
                    }

                    break;
                }

                string output = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                // Snapshot before write so we can detect buffer growth (MaxScrollback
                // increases when _terminal.Write adds lines; ScrollToBottom only moves
                // ViewportY and does not affect buffer length).
                int oldMax = MaxScrollback;
                int oldY = Terminal.Buffer.ViewportY;

                bool wasAtBottom = oldY >= oldMax;

                lock (_terminalLock)
                {
                    Terminal.Write(output);
                }

                // Auto-scroll to bottom when new content arrives, but only in normal buffer.
                // Alternate buffer (used by full-screen apps like vim, htop, asciiquarium)
                // handles its own cursor positioning and shouldn't be scrolled.
                if (!IsAlternateBuffer && wasAtBottom)
                {
                    Terminal.Buffer.ScrollToBottom();
                    int newY = Terminal.Buffer.ViewportY;
                    int newMax = MaxScrollback;

                    if (oldMax != newMax || oldY != newY)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (oldMax != newMax)
                            {
                                RaisePropertyChanged(MaxScrollbackProperty, oldMax, newMax);
                            }

                            if (oldY != newY)
                            {
                                RaisePropertyChanged(ViewportYProperty, oldY, newY);
                            }
                        });
                    }
                }
                else if (!IsAlternateBuffer && !wasAtBottom)
                {
                    int newMax = MaxScrollback;
                    if (oldMax != newMax)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            RaisePropertyChanged(MaxScrollbackProperty, oldMax, newMax);
                        });
                    }
                }

                // Notify IME of cursor position change after terminal processes data
                Dispatcher.UIThread.Post(() => _inputMethodClient?.NotifyCursorRectangleChanged());

                this.RequestInvalidate();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            // If the process has already exited the stream closing is expected — swallow silently.
            if (_processExitHandled != 0)
            {
                return;
            }

            lock (_terminalLock)
            {
                Terminal.WriteLine($"\nError reading from process: {ex.Message}\n");
                Terminal.Buffer.ScrollToBottom();
            }

            this.RequestInvalidate();
        }
    }

    private void OnPtyProcessExited(object? sender, PtyExitedEventArgs e)
    {
        // Interlocked ensures only one of (event, EOF path, exception path) prints the message.
        if (Interlocked.Exchange(ref _processExitHandled, 1) != 0)
        {
            return;
        }

        lock (_terminalLock)
        {
            Terminal.WriteLine($"\nProcess exited with code: {e.ExitCode}\n");
            Terminal.Buffer.ScrollToBottom();
        }

        this.RequestInvalidate();

        Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Raise event on UI thread so subscribers can safely update UI
            ProcessExitedEventArgs args = new(e.ExitCode);
            ProcessExited?.Invoke(this, args);
        });
    }

    private void CleanupProcess()
    {
        _processCts?.Cancel();

        if (_ptyConnection != null)
        {
            try
            {
                // Unsubscribe from event before cleanup
                _ptyConnection.ProcessExited -= OnPtyProcessExited;
                _ptyConnection.Kill();
                _ptyConnection.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }
            finally
            {
                _ptyConnection = null;
            }
        }

        _processCts?.Dispose();
        _processCts = null;
    }

    private void UpdateTextMetrics()
    {
        Typeface typeface = new(FontFamily, FontStyle, FontWeight);
        _measureText = new FormattedText(
            "W",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            Brushes.Black);
        _measureText.SetFontFeatures(FontFeatures);

        _charWidth = _measureText.Width;
        _charHeight = _measureText.Height;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        UpdateTextMetrics();

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Calculate how many columns fit in the allocated width
        if (_charWidth > 0)
        {
            int newCols = Math.Max(1, (int)(finalSize.Width / _charWidth));
            int newRows = Math.Max(1, (int)(finalSize.Height / _charHeight));

            // Only resize if dimensions have changed
            if (newCols != Terminal.Cols || newRows != Terminal.Rows)
            {
                lock (_terminalLock)
                {
                    int oldMax = MaxScrollback;
                    int oldY = ViewportY;

                    int offsetFromBottom = Math.Max(0, oldMax - oldY);
                    bool wasAtBottom = offsetFromBottom == 0;

                    Terminal.Resize(newCols, newRows);
                    ClearCache();

                    // Also resize the PTY connection if it exists
                    _ptyConnection?.Resize(newCols, newRows);

                    int newMax = MaxScrollback;
                    int targetY = wasAtBottom
                        ? newMax
                        : Math.Clamp(oldY, 0, newMax);

                    ViewportY = targetY;

                    RaisePropertyChanged(ViewportLinesProperty, 0, ViewportLines);
                    RaisePropertyChanged(MaxScrollbackProperty, oldMax, newMax);
                    RaisePropertyChanged(ViewportYProperty, oldY, ViewportY);
                }
            }
        }

        return finalSize;
    }


    public override void Render(DrawingContext context)
    {
        lock (_terminalLock)
        {
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            //Debug.WriteLine("======");
            //Debug.WriteLine(_terminal.Buffer.PrintViewport());

            // Use the terminal buffer's ViewportY to determine what to render
            int viewportY = Terminal.Buffer.ViewportY;
            int viewportLines = Terminal.Rows;
            int startLine = viewportY;
            int endLine = Math.Min(Terminal.Buffer.Length, startLine + viewportLines);

            try
            {
                for (int y = startLine; y < endLine; y++)
                {
                    BufferLine? line = Terminal.Buffer.GetLine(y);
                    if (line == null)
                    {
                        continue;
                    }

                    int screenY = y - startLine;

                    // Calculate Y positions for this screen row
                    double startYPos = Snap(screenY * _charHeight, scale);
                    double endYPos = Snap((screenY + 1) * _charHeight, scale);
                    double rowHeight = Math.Max(0, endYPos - startYPos);

                    // Check for double-width/double-height line attributes
                    LineAttribute lineAttr = line.LineAttribute;
                    if (lineAttr == LineAttribute.DoubleWidth ||
                        lineAttr == LineAttribute.DoubleHeightTop ||
                        lineAttr == LineAttribute.DoubleHeightBottom)
                    {
                        RenderDoubleWidthLine(context, line, startYPos, rowHeight, lineAttr, scale);
                    }
                    else
                    {
                        RenderNormalLine(context, line, startYPos, rowHeight, scale);
                    }
                }

                // Render selection overlay
                RenderSelection(context, scale);

                RenderCursor(context, viewportY, scale);

                // Render IME preedit (composition) text overlay
                RenderPreeditText(context, viewportY, scale);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TerminalView] Render error: {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Renders a normal (single-width, single-height) line.
    /// </summary>
    private void RenderNormalLine(DrawingContext context, BufferLine line, double startYPos,
        double rowHeight, double scale)
    {
        // Try to use cached text runs for this line (but not when ReverseVideo mode is active as it affects all cells)
        List<CachedTextRun>? textRuns = !Terminal.ReverseVideo ? line.Cache as List<CachedTextRun> : null;
        if (textRuns != null)
        {
            foreach (CachedTextRun run in textRuns)
            {
                // Recalculate position based on current screen row
                double startX = Snap(run.StartX * _charWidth, scale);
                double endX = Snap((run.StartX + run.CellCount) * _charWidth, scale);
                Rect rect = new(startX, startYPos, Math.Max(0, endX - startX), rowHeight);
                Point position = new(startX, startYPos);

                context.FillRectangle(run.Background, rect);
                context.DrawText(run.Text, position);
            }

            return;
        }

        // Build and cache text runs for this line
        textRuns = [];

        for (int x = 0; x < Terminal.Cols;)
        {
            if (x >= line.Length)
            {
                break;
            }

            BufferCell cell = line[x];
            string text = string.Empty;
            int cellCount = 0;
            int runStartX = 0;

            // Skip placeholder cells (width 0) that follow wide characters
            if (cell.Width == 0)
            {
                Debug.Assert(cell.Content == BufferCell.Empty.Content, "Placeholder cell should be null content");
                x++;
                continue;
            }

            if (cell.Width == 1)
            {
                // Collect consecutive cells with same attributes
                StringBuilder textBuilder = new();
                cellCount = 0; // Total cell positions consumed (including wide char placeholders)
                runStartX = x;
                while (x < line.Length && x < Terminal.Cols)
                {
                    BufferCell currentCell = line[x];

                    // Stop if we hit a different attribute or a placeholder cell mid-run
                    if (currentCell.Width != 1 || currentCell.Attributes != cell.Attributes)
                    {
                        break;
                    }

                    textBuilder.Append(currentCell.Content);
                    cellCount += currentCell.Width;

                    // Skip the placeholder cell that follows a wide character
                    x += currentCell.Width;
                }

                text = textBuilder.ToString();
            }
            else if (cell.Width == 2)
            {
                text = cell.Content;
                cellCount = cell.Width;
                runStartX = x;
                x += cell.Width; // Move past wide character and its placeholder
            }

            double startX = Snap(runStartX * _charWidth, scale);
            double endX = Snap((runStartX + cellCount) * _charWidth, scale);
            Rect rect = new(startX, startYPos, Math.Max(0, endX - startX), rowHeight);
            IBrush background = cell.GetBackgroundBrush(Background);
            IBrush foreground = cell.GetForegroundBrush(Foreground);
            // Apply cell-level inverse attribute
            if (cell.Attributes.IsInverse())
            {
                (foreground, background) = (background, foreground);
            }

            // Apply terminal-wide reverse video mode (DECSCNM)
            if (Terminal.ReverseVideo)
            {
                (foreground, background) = (background, foreground);
            }

            if (cell.Attributes.IsBlink() && _cursorBlinkOn)
            {
                (foreground, background) = (background, foreground);
            }

            Typeface typeface = new(FontFamily, cell.GetFontStyle(), cell.GetFontWeight());
            FormattedText formattedText = new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
                FontSize, foreground);
            TextDecorationCollection? td = cell.GetTextDecorations();
            if (td != null)
            {
                formattedText.SetTextDecorations(td);
            }

            formattedText.SetFontFeatures(FontFeatures);

            Point position = new(startX, startYPos);
            // Cache only content-dependent data, not screen position
            textRuns.Add(new CachedTextRun(formattedText, runStartX, cellCount, background));

            context.FillRectangle(background, rect);
            context.DrawText(formattedText, position);
        }

        // Cache the text runs (but not when ReverseVideo mode is active)
        if (!Terminal.ReverseVideo)
        {
            line.Cache = textRuns;
        }
    }

    /// <summary>
    ///     Renders a double-width or double-height line using transforms and clipping.
    /// </summary>
    private void RenderDoubleWidthLine(DrawingContext context, BufferLine line, double startYPos,
        double rowHeight, LineAttribute lineAttr, double scale)
    {
        // Don't cache double-width lines (transform makes caching complex)
        line.Cache = null;

        // Calculate the clip rect for this row
        Rect clipRect = new(0, startYPos, Terminal.Cols * _charWidth, rowHeight);

        // For double-height lines, we need to clip to show only top or bottom half
        double scaleX = 2.0;
        double scaleY = lineAttr.IsDoubleHeight() ? 2.0 : 1.0;

        // Calculate transform origin and translation
        // We scale from origin (0, startYPos) and then may need to shift for bottom half
        double translateY = 0;
        if (lineAttr == LineAttribute.DoubleHeightBottom)
        {
            // For bottom half, we render at 2x scale but shift up by one row height
            // so the bottom half of the scaled text is visible
            translateY = -rowHeight;
        }

        using (context.PushClip(clipRect))
        {
            // Create transform: scale 2x horizontally (and 2x vertically for double-height)
            // The transform origin is at (0, startYPos)
            Matrix scaleTransform = Matrix.CreateScale(scaleX, scaleY);
            Matrix translateToOrigin = Matrix.CreateTranslation(0, -startYPos);
            Matrix translateBack = Matrix.CreateTranslation(0, startYPos + translateY);
            Matrix combinedTransform = translateToOrigin * scaleTransform * translateBack;

            using (context.PushTransform(combinedTransform))
            {
                // Render the line content at normal size - the transform will scale it
                // Only render the first half of the columns since they'll be doubled
                int effectiveCols = Terminal.Cols / 2;

                for (int x = 0; x < effectiveCols && x < line.Length;)
                {
                    BufferCell cell = line[x];
                    string text = string.Empty;
                    int cellCount = 0;
                    int runStartX = 0;

                    // Skip placeholder cells (width 0) that follow wide characters
                    if (cell.Width == 0)
                    {
                        x++;
                        continue;
                    }

                    if (cell.Width == 1)
                    {
                        // Collect consecutive cells with same attributes
                        StringBuilder textBuilder = new();
                        cellCount = 0;
                        runStartX = x;
                        while (x < line.Length && x < effectiveCols)
                        {
                            BufferCell currentCell = line[x];
                            if (currentCell.Width != 1 || currentCell.Attributes != cell.Attributes)
                            {
                                break;
                            }

                            textBuilder.Append(currentCell.Content);
                            cellCount += currentCell.Width;
                            x += currentCell.Width;
                        }

                        text = textBuilder.ToString();
                    }
                    else if (cell.Width == 2)
                    {
                        text = cell.Content;
                        cellCount = cell.Width;
                        runStartX = x;
                        x += cell.Width;
                    }

                    double startX = Snap(runStartX * _charWidth, scale);
                    double endX = Snap((runStartX + cellCount) * _charWidth, scale);
                    Rect rect = new(startX, startYPos, Math.Max(0, endX - startX), rowHeight);
                    IBrush background = cell.GetBackgroundBrush(Background);
                    IBrush foreground = cell.GetForegroundBrush(Foreground);
                    // Apply cell-level inverse attribute
                    if (cell.Attributes.IsInverse())
                    {
                        (foreground, background) = (background, foreground);
                    }

                    // Apply terminal-wide reverse video mode (DECSCNM)
                    if (Terminal.ReverseVideo)
                    {
                        (foreground, background) = (background, foreground);
                    }

                    if (cell.Attributes.IsBlink() && _cursorBlinkOn)
                    {
                        (foreground, background) = (background, foreground);
                    }

                    Typeface typeface = new(FontFamily, cell.GetFontStyle(), cell.GetFontWeight());
                    FormattedText formattedText = new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        typeface, FontSize, foreground);
                    TextDecorationCollection? td = cell.GetTextDecorations();
                    if (td != null)
                    {
                        formattedText.SetTextDecorations(td);
                    }

                    formattedText.SetFontFeatures(FontFeatures);

                    Point position = new(startX, startYPos);

                    context.FillRectangle(background, rect);
                    context.DrawText(formattedText, position);
                }
            }
        }
    }

    /// <summary>
    ///     Renders the selection overlay.
    /// </summary>
    private void RenderSelection(DrawingContext context, double scale)
    {
        if (!Terminal.Selection.HasSelection)
        {
            return;
        }

        int viewportLines = Terminal.Rows;

        for (int screenY = 0; screenY < viewportLines; screenY++)
        {
            // Find cells that are selected in this row
            int? selectionStartX = null;
            int? selectionEndX = null;

            for (int x = 0; x < Terminal.Cols; x++)
            {
                if (Terminal.Selection.IsCellSelected(x, screenY))
                {
                    selectionStartX ??= x;

                    selectionEndX = x;
                }
                else if (selectionStartX.HasValue)
                {
                    // End of a selection run - draw it
                    DrawSelectionRect(context, selectionStartX.Value, selectionEndX!.Value + 1, screenY, scale);
                    selectionStartX = null;
                    selectionEndX = null;
                }
            }

            // Draw remaining selection at end of row
            if (selectionStartX.HasValue)
            {
                DrawSelectionRect(context, selectionStartX.Value, selectionEndX!.Value + 1, screenY, scale);
            }
        }
    }

    private void DrawSelectionRect(DrawingContext context, int startX, int endX, int screenY, double scale)
    {
        double x1 = Snap(startX * _charWidth, scale);
        double x2 = Snap(endX * _charWidth, scale);
        double y1 = Snap(screenY * _charHeight, scale);
        double y2 = Snap((screenY + 1) * _charHeight, scale);

        Rect rect = new(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
        context.FillRectangle(SelectionBrush, rect);
    }

    private void RenderCursor(DrawingContext context, int viewportY, double scale)
    {
        // Only show cursor if terminal wants it visible (controlled by escape sequences)
        if (!Terminal.CursorVisible)
        {
            return;
        }

        // Only show cursor if in "on" phase of blink cycle (or not blinking)
        if (!_cursorBlinkOn)
        {
            return;
        }

        // Get cursor position relative to viewport
        int cursorX = Terminal.Buffer.X;
        int cursorY = Terminal.Buffer.Y;

        // The cursor Y is relative to the active screen area, need to check if it's visible
        // when scrolled. Cursor is at absolute position: Buffer.YBase + Buffer.Y
        int absoluteCursorY = Terminal.Buffer.YBase + cursorY;

        // Check if cursor is visible in current viewport
        if (absoluteCursorY < viewportY || absoluteCursorY >= viewportY + Terminal.Rows)
        {
            return;
        }

        // Calculate screen position
        int screenY = absoluteCursorY - viewportY;
        double posX = Snap(cursorX * _charWidth, scale);
        double posY = Snap(screenY * _charHeight, scale);
        double nextX = Snap((cursorX + 1) * _charWidth, scale);
        double nextY = Snap((screenY + 1) * _charHeight, scale);
        double cellWidth = Math.Max(0, nextX - posX);
        double cellHeight = Math.Max(0, nextY - posY);

        SolidColorBrush cursorBrush = new(CursorColor);

        // Render based on cursor style (use property which syncs with terminal)
        switch (CursorStyle)
        {
            case CursorStyle.Block:
            {
                // TODO Use ConsoleFontBrush
                if (IsFocused)
                {
                    // Filled block when focused
                    context.FillRectangle(cursorBrush, new Rect(posX, posY, cellWidth, cellHeight));

                    // Draw the character under cursor with inverted colors
                    BufferLine? line = Terminal.Buffer.GetLine(absoluteCursorY);
                    if (line != null && cursorX < line.Length)
                    {
                        BufferCell cell = line[cursorX];
                        string charContent = cell.Content;
                        Typeface typeface = new(FontFamily, FontStyle, FontWeight);
                        IBrush invertedBrush = cell.GetBackgroundBrush(Background);
                        FormattedText formattedText = new(
                            charContent,
                            CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight,
                            typeface,
                            FontSize,
                            invertedBrush);
                        formattedText.SetFontFeatures(FontFeatures);
                        context.DrawText(formattedText, new Point(posX, posY));
                    }
                }
                else
                {
                    // Outline block when not focused
                    Pen pen = new(cursorBrush);
                    context.DrawRectangle(pen, new Rect(posX, posY, cellWidth, cellHeight));
                }

                break;
            }

            case CursorStyle.Underline:
            {
                // Draw underline cursor (2 pixels high at bottom of cell)
                double underlineHeight = Math.Min(2.0, cellHeight);
                context.FillRectangle(cursorBrush,
                    new Rect(posX, posY + cellHeight - underlineHeight, cellWidth, underlineHeight));

                break;
            }

            case CursorStyle.Bar:
            {
                // Draw bar cursor (2 pixels wide at left of cell)
                double barWidth = Math.Min(2.0, cellWidth);
                context.FillRectangle(cursorBrush, new Rect(posX, posY, barWidth, cellHeight));

                break;
            }
        }
    }

    private static double Snap(double value, double scale)
    {
        return Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;
    }

    /// <summary>
    ///     Renders the IME preedit (composition) text overlay at the cursor position.
    ///     This displays the uncommitted text that the IME is composing, with an underline
    ///     to indicate it is not yet committed.
    /// </summary>
    private void RenderPreeditText(DrawingContext context, int viewportY, double scale)
    {
        string? preeditText = _inputMethodClient?.PreeditText;
        if (string.IsNullOrEmpty(preeditText))
        {
            return;
        }

        int cursorX = Terminal.Buffer.X;
        int cursorY = Terminal.Buffer.Y;
        int absoluteCursorY = Terminal.Buffer.YBase + cursorY;

        // Only render if cursor is visible in current viewport
        if (absoluteCursorY < viewportY || absoluteCursorY >= viewportY + Terminal.Rows)
        {
            return;
        }

        int screenY = absoluteCursorY - viewportY;
        double posX = Snap(cursorX * _charWidth, scale);
        double posY = Snap(screenY * _charHeight, scale);
        double cellHeight = Snap((screenY + 1) * _charHeight, scale) - posY;

        Typeface typeface = new(FontFamily, FontStyle, FontWeight);
        IBrush foreground = GetValue(ForegroundProperty);
        IBrush background = GetValue(BackgroundProperty);

        FormattedText formattedText = new(
            preeditText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            foreground);

        formattedText.SetFontFeatures(FontFeatures);

        double textWidth = formattedText.Width;

        // Draw background behind preedit text to cover existing content
        context.FillRectangle(background, new Rect(posX, posY, textWidth, cellHeight));

        // Draw the preedit text
        context.DrawText(formattedText, new Point(posX, posY));

        // Draw underline to indicate uncommitted composition text
        double underlineY = posY + cellHeight - Math.Max(1.0, scale);
        Pen pen = new(foreground, Math.Max(1.0, scale));
        context.DrawLine(pen, new Point(posX, underlineY), new Point(posX + textWidth, underlineY));
    }

    public async Task ClearAsync()
    {
        lock (_terminalLock)
        {
            Terminal.ClearScrollback();

            ClearCache();
        }

        Dispatcher.UIThread.Post(() =>
        {
            RaisePropertyChanged(MaxScrollbackProperty, 0, MaxScrollback);
            RaisePropertyChanged(ViewportYProperty, 0, ViewportY);
            this.RequestInvalidate();
        });

        if (_ptyConnection != null)
        {
            string processName = Process;

            if (processName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
                processName.EndsWith("cmd", StringComparison.OrdinalIgnoreCase))
            {
                await SendToPtyAsync("cls\r");
            }
            else
            {
                await SendToPtyAsync("\f"); // Ctrl+L (Form Feed)
            }
        }
    }

    private sealed record CachedTextRun(FormattedText Text, int StartX, int CellCount, IBrush Background);

    #region IME Support

    /// <summary>
    ///     Implements Avalonia's <see cref="TextInputMethodClient" /> for the terminal.
    ///     This enables IME (Input Method Editor) support so that non-English characters
    ///     can be composed correctly with the composition window positioned at the cursor.
    /// </summary>
    private sealed class TerminalInputMethodClient(TerminalView view) : TextInputMethodClient
    {
        /// <summary>
        ///     Gets the preedit (composition) text currently being entered by the IME.
        /// </summary>
        public string? PreeditText { get; private set; }

        /// <summary>
        ///     The visual that is rendering the text — this is the terminal view itself.
        /// </summary>
        public override Visual TextViewVisual => view;

        /// <summary>
        ///     Indicates the terminal supports displaying uncommitted preedit text.
        /// </summary>
        public override bool SupportsPreedit => true;

        /// <summary>
        ///     Indicates the terminal can provide surrounding text for IME context.
        /// </summary>
        public override bool SupportsSurroundingText => true;

        /// <summary>
        ///     Returns the text content of the current line up to the cursor,
        ///     providing context for the IME.
        /// </summary>
        public override string SurroundingText
        {
            get
            {
                try
                {
                    TerminalBuffer buffer = view.Terminal.Buffer;
                    int absoluteY = buffer.YBase + buffer.Y;
                    BufferLine? line = buffer.GetLine(absoluteY);
                    if (line == null)
                    {
                        return string.Empty;
                    }

                    StringBuilder sb = new();
                    foreach (BufferCell cell in line)
                    {
                        sb.Append(string.IsNullOrEmpty(cell.Content) ? " " : cell.Content);
                    }

                    return sb.ToString();
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>
        ///     Gets the cursor rectangle relative to the terminal view,
        ///     used to position the IME composition window at the cursor.
        /// </summary>
        public override Rect CursorRectangle
        {
            get
            {
                try
                {
                    TerminalBuffer buffer = view.Terminal.Buffer;
                    int cursorX = buffer.X;
                    int absoluteCursorY = buffer.YBase + buffer.Y;
                    int viewportY = buffer.ViewportY;
                    int screenY = absoluteCursorY - viewportY;

                    double posX = cursorX * view._charWidth;
                    double posY = screenY * view._charHeight;

                    return new Rect(posX, posY, view._charWidth, view._charHeight);
                }
                catch
                {
                    return default;
                }
            }
        }

        /// <summary>
        ///     Gets or sets the selection range within the surrounding text.
        ///     For a terminal, this corresponds to the cursor column position.
        /// </summary>
        public override TextSelection Selection
        {
            get
            {
                try
                {
                    int cursorX = view.Terminal.Buffer.X;
                    return new TextSelection(cursorX, cursorX);
                }
                catch
                {
                    return new TextSelection(0, 0);
                }
            }
            set
            {
                /* Terminal selection is managed separately */
            }
        }

        /// <summary>
        ///     Called by the IME to display uncommitted composition text at the cursor position.
        /// </summary>
        public override void SetPreeditText(string? preeditText)
        {
            PreeditText = preeditText;
            view.RequestInvalidate();
        }

        /// <summary>
        ///     Called by the IME to display uncommitted composition text with an optional
        ///     cursor offset within the preedit string.
        /// </summary>
        /// <param name="preeditText">The current composition text, or null/empty to clear it.</param>
        /// <param name="cursorPos">
        ///     The cursor position within the preedit string.
        ///     A terminal renders preedit as a simple underlined overlay so the within-composition
        ///     cursor position is not used here.
        /// </param>
        public override void SetPreeditText(string? preeditText, int? cursorPos)
        {
            // cursorPos (position of IME cursor within the composition string) is intentionally
            // not used: the terminal renders preedit as a simple underlined text overlay and
            // does not support a separate cursor inside the composition window.
            PreeditText = preeditText;
            view.RequestInvalidate();
        }

        /// <summary>
        ///     Clears any active preedit text (e.g. when focus is lost).
        /// </summary>
        public void ClearPreeditText()
        {
            if (PreeditText != null)
            {
                PreeditText = null;
                view.RequestInvalidate();
            }
        }

        /// <summary>
        ///     Notifies the IME that the cursor rectangle has changed.
        ///     Called when the terminal buffer updates and the cursor may have moved.
        /// </summary>
        internal void NotifyCursorRectangleChanged()
        {
            RaiseCursorRectangleChanged();
        }
    }

    #endregion

    #region Terminal Attached Events

    public static readonly RoutedEvent<TitleChangedEventArgs> TitleChangedEvent =
        RoutedEvent.Register<TerminalView, TitleChangedEventArgs>(
            nameof(TitleChanged),
            RoutingStrategies.Bubble);

    public static void AddTitleChangedHandler(Interactive target, EventHandler<TitleChangedEventArgs> handler)
    {
        target.AddHandler(TitleChangedEvent, handler);
    }

    public static void RemoveTitleChangedHandler(Interactive target, EventHandler<TitleChangedEventArgs> handler)
    {
        target.RemoveHandler(TitleChangedEvent, handler);
    }

    public static readonly RoutedEvent<WindowMovedEventArgs> WindowMovedEvent =
        RoutedEvent.Register<TerminalView, WindowMovedEventArgs>(
            nameof(WindowMoved),
            RoutingStrategies.Bubble);

    public static void AddWindowMovedHandler(Interactive target, EventHandler<WindowMovedEventArgs> handler)
    {
        target.AddHandler(WindowMovedEvent, handler);
    }

    public static void RemoveWindowMovedHandler(Interactive target, EventHandler<WindowMovedEventArgs> handler)
    {
        target.RemoveHandler(WindowMovedEvent, handler);
    }

    public static readonly RoutedEvent<WindowResizedEventArgs> WindowResizedEvent =
        RoutedEvent.Register<TerminalView, WindowResizedEventArgs>(
            nameof(WindowResized),
            RoutingStrategies.Bubble);

    public static void AddWindowResizedHandler(Interactive target, EventHandler<WindowResizedEventArgs> handler)
    {
        target.AddHandler(WindowResizedEvent, handler);
    }

    public static void RemoveWindowResizedHandler(Interactive target, EventHandler<WindowResizedEventArgs> handler)
    {
        target.RemoveHandler(WindowResizedEvent, handler);
    }

    public static readonly RoutedEvent<RoutedEventArgs> WindowMinimizedEvent =
        RoutedEvent.Register<TerminalView, RoutedEventArgs>(
            nameof(WindowMinimized),
            RoutingStrategies.Bubble);

    public static void AddWindowMinimizedHandler(Interactive target, EventHandler<RoutedEventArgs> handler)
    {
        target.AddHandler(WindowMinimizedEvent, handler);
    }

    public static void RemoveWindowMinimizedHandler(Interactive target, EventHandler<RoutedEventArgs> handler)
    {
        target.RemoveHandler(WindowMinimizedEvent, handler);
    }

    public static readonly RoutedEvent<RoutedEventArgs> WindowMaximizedEvent =
        RoutedEvent.Register<TerminalView, RoutedEventArgs>(
            nameof(WindowMaximized),
            RoutingStrategies.Bubble);

    public static void AddWindowMaximizedHandler(Interactive target, EventHandler<RoutedEventArgs> handler)
    {
        target.AddHandler(WindowMaximizedEvent, handler);
    }

    public static void RemoveWindowMaximizedHandler(Interactive target, EventHandler<RoutedEventArgs> handler)
    {
        target.RemoveHandler(WindowMaximizedEvent, handler);
    }

    public static readonly RoutedEvent<RoutedEventArgs> WindowRestoredEvent =
        RoutedEvent.Register<TerminalView, RoutedEventArgs>(
            nameof(WindowRestored),
            RoutingStrategies.Bubble);

    public static void AddWindowRestoredHandler(Interactive target, EventHandler<RoutedEventArgs> handler)
    {
        target.AddHandler(WindowRestoredEvent, handler);
    }

    public static void RemoveWindowRestoredHandler(Interactive target, EventHandler<RoutedEventArgs> handler)
    {
        target.RemoveHandler(WindowRestoredEvent, handler);
    }

    public static readonly RoutedEvent<RoutedEventArgs> WindowRaisedEvent =
        RoutedEvent.Register<TerminalView, RoutedEventArgs>(
            nameof(WindowRaised),
            RoutingStrategies.Bubble);

    public static void AddWindowRaisedHandler(Interactive target, EventHandler<RoutedEventArgs> handler)
    {
        target.AddHandler(WindowRaisedEvent, handler);
    }

    public static void RemoveWindowRaisedHandler(Interactive target, EventHandler<RoutedEventArgs> handler)
    {
        target.RemoveHandler(WindowRaisedEvent, handler);
    }

    public static readonly RoutedEvent<RoutedEventArgs> WindowLoweredEvent =
        RoutedEvent.Register<TerminalView, RoutedEventArgs>(
            nameof(WindowLowered),
            RoutingStrategies.Bubble);

    public static void AddWindowLoweredHandler(Interactive target, EventHandler<RoutedEventArgs> handler)
    {
        target.AddHandler(WindowLoweredEvent, handler);
    }

    public static void RemoveWindowLoweredHandler(Interactive target, EventHandler<RoutedEventArgs> handler)
    {
        target.RemoveHandler(WindowLoweredEvent, handler);
    }

    public static readonly RoutedEvent<RoutedEventArgs> WindowFullscreenedEvent =
        RoutedEvent.Register<TerminalView, RoutedEventArgs>(
            nameof(WindowFullscreened),
            RoutingStrategies.Bubble);

    public static void AddWindowFullscreenedHandler(Interactive target, EventHandler<RoutedEventArgs> handler)
    {
        target.AddHandler(WindowFullscreenedEvent, handler);
    }

    public static void RemoveWindowFullscreenedHandler(Interactive target, EventHandler<RoutedEventArgs> handler)
    {
        target.RemoveHandler(WindowFullscreenedEvent, handler);
    }

    public static readonly RoutedEvent<RoutedEventArgs> BellRangEvent =
        RoutedEvent.Register<TerminalView, RoutedEventArgs>(
            nameof(BellRang),
            RoutingStrategies.Bubble);

    public static void AddBellRangHandler(Interactive target, EventHandler<RoutedEventArgs> handler)
    {
        target.AddHandler(BellRangEvent, handler);
    }

    public static void RemoveBellRangHandler(Interactive target, EventHandler<RoutedEventArgs> handler)
    {
        target.RemoveHandler(BellRangEvent, handler);
    }

    public static readonly RoutedEvent<WindowInfoRequestedEventArgs> WindowInfoRequestedEvent =
        RoutedEvent.Register<TerminalView, WindowInfoRequestedEventArgs>(
            nameof(WindowInfoRequested),
            RoutingStrategies.Bubble);

    public static void AddWindowInfoRequestedHandler(Interactive target,
        EventHandler<WindowInfoRequestedEventArgs> handler)
    {
        target.AddHandler(WindowInfoRequestedEvent, handler);
    }

    public static void RemoveWindowInfoRequestedHandler(Interactive target,
        EventHandler<WindowInfoRequestedEventArgs> handler)
    {
        target.RemoveHandler(WindowInfoRequestedEvent, handler);
    }

    #endregion

    #region Win32 Input Mode Support

    /// <summary>
    ///     Generates a Win32 INPUT_RECORD format escape sequence.
    ///     Format: ESC [ Vk ; Sc ; Uc ; Kd ; Cs ; Rc _
    /// </summary>
    private string GenerateWin32InputSequence(KeyEventArgs e, bool isKeyDown)
    {
        int vk = ConvertAvaloniaKeyToVirtualKey(e.Key);

        // If we can't get a virtual key code, we can't generate a Win32 sequence
        if (vk == 0)
        {
            Debug.WriteLine($"[TerminalView] Win32: No VK for Key={e.Key}");
            return string.Empty;
        }

        // Get scan code (we use 0 as we don't have direct access to hardware scan codes)
        int scanCode = 0;

        // Get unicode character - first try KeySymbol, then fall back to key mapping
        // Note: Special keys (arrows, Enter, etc.) have unicodeChar=0 which is correct
        int unicodeChar = 0;
        if (!string.IsNullOrEmpty(e.KeySymbol) && e.KeySymbol.Length >= 1)
        {
            unicodeChar = char.ConvertToUtf32(e.KeySymbol, 0);
        }
        else if (TryMapKeyToChar(e.Key, e.KeyModifiers, out char mappedChar))
        {
            // Fallback for Consolonia where KeySymbol is empty
            unicodeChar = mappedChar;
        }
        // Special case: Enter key should send CR (0x0D)
        else if (e.Key == Key.Enter)
        {
            unicodeChar = 0x0D;
        }
        // Special case: Tab key should send Tab (0x09)
        else if (e.Key == Key.Tab)
        {
            unicodeChar = 0x09;
        }
        // Special case: Backspace should send BS (0x08)
        else if (e.Key == Key.Back)
        {
            unicodeChar = 0x08;
        }
        // Special case: Escape should send ESC (0x1B)
        else if (e.Key == Key.Escape)
        {
            unicodeChar = 0x1B;
        }
        // Special case: Space
        else if (e.Key == Key.Space)
        {
            unicodeChar = 0x20;
        }

        // If Ctrl is pressed and this is a printable character, prefer the corresponding control code.
        // This improves compatibility for terminal apps that expect ^X (0x18), ^C (0x03), etc.
        // even when the underlying transport is Win32 INPUT_RECORD format.
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && unicodeChar != 0)
        {
            // Ctrl+A..Z => 0x01..0x1A
            if (unicodeChar is >= 'a' and <= 'z')
            {
                unicodeChar = unicodeChar - 'a' + 1;
            }
            else if (unicodeChar is >= 'A' and <= 'Z')
            {
                unicodeChar = unicodeChar - 'A' + 1;
            }
            else
            {
                // Common Ctrl+<punct> mappings
                unicodeChar = unicodeChar switch
                {
                    0x20 => 0x00, // Ctrl+Space => NUL
                    '@' => 0x00, // Ctrl+@ => NUL
                    '[' => 0x1B, // Ctrl+[ => ESC
                    '\\' => 0x1C, // Ctrl+\\ => FS
                    ']' => 0x1D, // Ctrl+] => GS
                    '^' => 0x1E, // Ctrl+^ => RS
                    '_' => 0x1F, // Ctrl+_ => US
                    '?' => 0x7F, // Ctrl+? => DEL
                    _ => unicodeChar
                };
            }
        }

        // Build control key state flags
        Win32ControlKeyState controlKeyState = GetWin32ControlKeyState(e.KeyModifiers, e.Key);

        // Repeat count (always 1 for our purposes)
        int repeatCount = 1;

        // Format: ESC [ Vk ; Sc ; Uc ; Kd ; Cs ; Rc _
        return $"\e[{vk};{scanCode};{unicodeChar};{(isKeyDown ? 1 : 0)};{(int)controlKeyState};{repeatCount}_";
    }

    /// <summary>
    ///     Converts Avalonia KeyModifiers to Win32 control key state flags.
    /// </summary>
    private static Win32ControlKeyState GetWin32ControlKeyState(KeyModifiers modifiers, Key key)
    {
        Win32ControlKeyState state = Win32ControlKeyState.None;

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            state |= Win32ControlKeyState.ShiftPressed;
        }

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            state |= Win32ControlKeyState.LeftCtrlPressed;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            state |= Win32ControlKeyState.LeftAltPressed;
        }

        // Mark enhanced keys (navigation keys, etc.)
        if (IsEnhancedKey(key))
        {
            state |= Win32ControlKeyState.EnhancedKey;
        }

        return state;
    }

    /// <summary>
    ///     Determines if a key is an "enhanced" key (extended keyboard keys).
    /// </summary>
    private static bool IsEnhancedKey(Key key)
    {
        return key switch
        {
            Key.Insert or Key.Delete or Key.Home or Key.End or
                Key.PageUp or Key.PageDown or Key.Up or Key.Down or
                Key.Left or Key.Right or Key.Divide or
                Key.NumLock or Key.RightCtrl or Key.RightAlt or
                Key.PrintScreen or Key.Pause => true,
            _ => false
        };
    }

    /// <summary>
    ///     Converts Avalonia Key to Windows Virtual Key code.
    /// </summary>
    private static int ConvertAvaloniaKeyToVirtualKey(Key key)
    {
        return key switch
        {
            // Letters
            Key.A => 0x41,
            Key.B => 0x42,
            Key.C => 0x43,
            Key.D => 0x44,
            Key.E => 0x45,
            Key.F => 0x46,
            Key.G => 0x47,
            Key.H => 0x48,
            Key.I => 0x49,
            Key.J => 0x4A,
            Key.K => 0x4B,
            Key.L => 0x4C,
            Key.M => 0x4D,
            Key.N => 0x4E,
            Key.O => 0x4F,
            Key.P => 0x50,
            Key.Q => 0x51,
            Key.R => 0x52,
            Key.S => 0x53,
            Key.T => 0x54,
            Key.U => 0x55,
            Key.V => 0x56,
            Key.W => 0x57,
            Key.X => 0x58,
            Key.Y => 0x59,
            Key.Z => 0x5A,

            // Numbers
            Key.D0 => 0x30,
            Key.D1 => 0x31,
            Key.D2 => 0x32,
            Key.D3 => 0x33,
            Key.D4 => 0x34,
            Key.D5 => 0x35,
            Key.D6 => 0x36,
            Key.D7 => 0x37,
            Key.D8 => 0x38,
            Key.D9 => 0x39,

            // Function keys
            Key.F1 => 0x70,
            Key.F2 => 0x71,
            Key.F3 => 0x72,
            Key.F4 => 0x73,
            Key.F5 => 0x74,
            Key.F6 => 0x75,
            Key.F7 => 0x76,
            Key.F8 => 0x77,
            Key.F9 => 0x78,
            Key.F10 => 0x79,
            Key.F11 => 0x7A,
            Key.F12 => 0x7B,
            Key.F13 => 0x7C,
            Key.F14 => 0x7D,
            Key.F15 => 0x7E,
            Key.F16 => 0x7F,
            Key.F17 => 0x80,
            Key.F18 => 0x81,
            Key.F19 => 0x82,
            Key.F20 => 0x83,
            Key.F21 => 0x84,
            Key.F22 => 0x85,
            Key.F23 => 0x86,
            Key.F24 => 0x87,

            // Navigation keys
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,

            // Control keys
            Key.Back => 0x08,
            Key.Tab => 0x09,
            Key.Enter => 0x0D,
            Key.Escape => 0x1B,
            Key.Space => 0x20,
            Key.Pause => 0x13,
            Key.CapsLock => 0x14,
            Key.NumLock => 0x90,
            Key.Scroll => 0x91,
            Key.PrintScreen => 0x2C,

            // Modifier keys
            Key.LeftShift => 0x10,
            Key.RightShift => 0x10,
            Key.LeftCtrl => 0x11,
            Key.RightCtrl => 0x11,
            Key.LeftAlt => 0x12,
            Key.RightAlt => 0x12,
            Key.LWin => 0x5B,
            Key.RWin => 0x5C,

            // Numpad
            Key.NumPad0 => 0x60,
            Key.NumPad1 => 0x61,
            Key.NumPad2 => 0x62,
            Key.NumPad3 => 0x63,
            Key.NumPad4 => 0x64,
            Key.NumPad5 => 0x65,
            Key.NumPad6 => 0x66,
            Key.NumPad7 => 0x67,
            Key.NumPad8 => 0x68,
            Key.NumPad9 => 0x69,
            Key.Multiply => 0x6A,
            Key.Add => 0x6B,
            Key.Separator => 0x6C,
            Key.Subtract => 0x6D,
            Key.Decimal => 0x6E,
            Key.Divide => 0x6F,

            // OEM keys
            Key.OemSemicolon => 0xBA,
            Key.OemPlus => 0xBB,
            Key.OemComma => 0xBC,
            Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF,
            Key.OemTilde => 0xC0,
            Key.OemOpenBrackets => 0xDB,
            Key.OemPipe => 0xDC,
            Key.OemCloseBrackets => 0xDD,
            Key.OemQuotes => 0xDE,
            Key.OemBackslash => 0xE2,

            _ => 0
        };
    }

    #endregion

    #region Live Color Refresh Support

    private static readonly List<WeakReference<TerminalView>> ActiveViews = [];

    private static void RegisterActiveView(TerminalView view)
    {
        lock (ActiveViews)
        {
            if (!ActiveViews.Any(wr => wr.TryGetTarget(out TerminalView? target) && target == view))
            {
                ActiveViews.Add(new WeakReference<TerminalView>(view));
            }
        }
    }

    private static void UnregisterActiveView(TerminalView view)
    {
        lock (ActiveViews)
        {
            ActiveViews.RemoveAll(wr => !wr.TryGetTarget(out TerminalView? target) || target == view);
        }
    }

    /// <summary>
    ///     Clears the rendering cache for all active TerminalView instances.
    /// </summary>
    public static void ClearAllCaches()
    {
        lock (ActiveViews)
        {
            for (int i = ActiveViews.Count - 1; i >= 0; i--)
            {
                if (ActiveViews[i].TryGetTarget(out TerminalView? view))
                {
                    view.ClearCache();
                }
                else
                {
                    ActiveViews.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    ///     Clears the rendering cache for this TerminalView instance.
    /// </summary>
    public void ClearCache()
    {
        lock (_terminalLock)
        {
            if (Terminal?.Buffer != null)
            {
                for (int i = 0; i < Terminal.Buffer.Length; i++)
                {
                    BufferLine? line = Terminal.Buffer.GetLine(i);
                    if (line != null)
                    {
                        line.Cache = null;
                    }
                }
            }
        }

        this.RequestInvalidate();
    }

    #endregion
}
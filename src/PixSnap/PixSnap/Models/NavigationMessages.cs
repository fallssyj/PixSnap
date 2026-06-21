using System.Windows.Input;

namespace PixSnap.Models;

public sealed class ShowSettingsMessage;

public sealed class ShowLogViewerMessage;

public sealed class ShowAboutMessage;

public sealed class StartCaptureMessage;

public sealed class RecaptureMessage;

public sealed class ShutdownApplicationMessage;

public sealed record HotkeyChangedMessage(ModifierKeys Modifiers, Key Key);

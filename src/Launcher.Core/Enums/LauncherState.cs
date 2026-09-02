namespace Launcher.Core.Enums;

public enum LauncherState
{
    CheckingForUpdates,
    NotInstalled,
    UpdateAvailable,
    Downloading,
    Patching,
    Verifying,
    ReadyToPlay,
    GameRunning,
    Error
}

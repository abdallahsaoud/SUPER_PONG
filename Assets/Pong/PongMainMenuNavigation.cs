using System;

/// <summary>
/// Entry point for returning to the main menu from in-game UI.
/// Subscribe to <see cref="OnMainMenuRequested"/> from the main-menu scene/bootstrap when ready.
/// </summary>
public static class PongMainMenuNavigation
{
    /// <summary>Raised when the player chooses to leave for the main menu.</summary>
    public static event Action OnMainMenuRequested;

    public static void RequestMainMenu()
    {
        OnMainMenuRequested?.Invoke();
    }
}

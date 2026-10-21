using System;

namespace ScriptDock.Services;

/// <summary>
/// Whether a script that exits with a code other than 0 reads as an error. Off, the default, such a run
/// reads as any other exit; on, Recent shows it in red with its code and Records lists it as an error. The
/// code is recorded either way, and every run, an old one included, is read under the setting as it is now.
/// A run that could not start reads as an error whatever the setting.
/// </summary>
public static class ExitCodes
{
    /// <summary>Whether an exit code other than 0 reads as an error now.</summary>
    public static bool NonZeroIsError { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="NonZeroIsError"/> changes, so shown runs are read again.</summary>
    public static event Action? Changed;

    /// <summary>Reads exit codes under <paramref name="nonZeroIsError"/> from now on.</summary>
    public static void Use(bool nonZeroIsError)
    {
        if (nonZeroIsError == NonZeroIsError)
            return;
        NonZeroIsError = nonZeroIsError;
        Changed?.Invoke();
    }
}

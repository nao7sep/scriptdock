using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// Which closes of the main window are the operating system ending the session, which never asks
/// anything, and which are quits the user started (unsaved-edits-conventions, Quitting).
/// </summary>
public sealed class SessionEndTests
{
    [MacOnlyFact]
    public void Native_quit_reason_is_an_event_parameter()
    {
        NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        using var pool = new ObjC.OwnedObject(ObjC.Send(ObjC.Class("NSAutoreleasePool"), "new"));
        var descriptor = ObjC.Class("NSAppleEventDescriptor");
        var quit = CreateEvent(descriptor, ObjC.Sel("appleEventWithEventClass:eventID:targetDescriptor:returnID:transactionID:"),
            0x61657674, 0x71756974, IntPtr.Zero, -1, 0); // aevt / quit; never sent
        Assert.NotEqual(IntPtr.Zero, quit);
        Assert.False(SessionEnd.MacQuitIsSessionEnd(IntPtr.Zero));
        Assert.False(SessionEnd.MacQuitIsSessionEnd(quit));
        foreach (var code in new uint[] { 0x72657374, 0x73687574, 0x726C676F, 0x71756961 }) // rest, shut, rlgo, quia
        {
            var reason = ObjC.SendWithCode(descriptor, "descriptorWithEnumCode:", code);
            SetParameter(quit, ObjC.Sel("setParamDescriptor:forKeyword:"), reason, 0x7768793F);
            Assert.True(SessionEnd.MacQuitIsSessionEnd(quit));
        }
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr CreateEvent(IntPtr receiver, IntPtr selector, uint eventClass, uint eventId,
        IntPtr target, short returnId, int transactionId);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SetParameter(IntPtr receiver, IntPtr selector, IntPtr descriptor, uint keyword);

    [Theory]
    // A Windows shutdown or restart, and a Windows logoff, which Avalonia reports as an app shutdown.
    [InlineData(WindowCloseReason.OSShutdown, true, false, true)]
    [InlineData(WindowCloseReason.ApplicationShutdown, true, false, true)]
    // macOS: a logout, restart or shutdown carries its reason on the quit event; the Dock's quit and the
    // menu's Quit and Cmd+Q do not.
    [InlineData(WindowCloseReason.ApplicationShutdown, false, true, true)]
    [InlineData(WindowCloseReason.ApplicationShutdown, false, false, false)]
    // The window's own close button or shortcut, on either system.
    [InlineData(WindowCloseReason.WindowClosing, true, false, false)]
    [InlineData(WindowCloseReason.WindowClosing, false, true, false)]
    [InlineData(WindowCloseReason.Undefined, false, false, false)]
    public void Only_the_system_ending_the_session_is_a_session_end(
        WindowCloseReason reason, bool windows, bool macQuitEventHasReason, bool sessionEnd) =>
        Assert.Equal(sessionEnd, SessionEnd.Is(reason, windows, () => macQuitEventHasReason));

    [Fact]
    public void The_quit_event_is_read_only_for_a_shutdown_the_lifetime_reports()
    {
        var read = 0;
        SessionEnd.Is(WindowCloseReason.WindowClosing, windows: false, () => { read++; return true; });
        SessionEnd.Is(WindowCloseReason.OSShutdown, windows: false, () => { read++; return true; });
        Assert.Equal(0, read);
    }
}

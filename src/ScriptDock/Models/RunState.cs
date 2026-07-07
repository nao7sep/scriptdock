namespace ScriptDock.Models;

/// <summary>Lifecycle of a launched script as ScriptDock owns it.</summary>
public enum RunState
{
    /// <summary>The process is alive.</summary>
    Running,

    /// <summary>The process ended on its own.</summary>
    Exited,

    /// <summary>ScriptDock terminated the process (a stop or a restart).</summary>
    Terminated,

    /// <summary>The process could not be started.</summary>
    Failed,
}

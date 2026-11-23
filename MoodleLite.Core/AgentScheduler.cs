using Microsoft.Win32.TaskScheduler;

namespace MoodleLite.Core;

/// <summary>Registers/removes the scheduled task that runs MoodleLite.Agent.exe (interactive, current user only).</summary>
public static class AgentScheduler
{
    const string TaskName = "MoodleLite Agent";

    public static bool IsRegistered()
    {
        using var ts = new TaskService();
        return ts.GetTask(TaskName) != null;
    }

    public static void Register(string agentExePath, int intervalMinutes)
    {
        using var ts = new TaskService();
        var td = ts.NewTask();
        td.RegistrationInfo.Description = "MoodleLite background sync and notifications";
        // "Run only when user is logged on" (otherwise toasts don't show)
        td.Principal.LogonType = TaskLogonType.InteractiveToken;
        td.Principal.RunLevel = TaskRunLevel.LUA;
        td.Settings.DisallowStartIfOnBatteries = false;
        td.Settings.StopIfGoingOnBatteries = false;
        td.Settings.StartWhenAvailable = true;
        td.Settings.Hidden = true;
        td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        td.Settings.ExecutionTimeLimit = TimeSpan.FromMinutes(10);

        td.Triggers.Add(new LogonTrigger { UserId = System.Security.Principal.WindowsIdentity.GetCurrent().Name });
        td.Triggers.Add(new TimeTrigger
        {
            StartBoundary = DateTime.Now,
            Repetition = new RepetitionPattern(TimeSpan.FromMinutes(Math.Max(5, intervalMinutes)), TimeSpan.Zero),
        });
        td.Actions.Add(new ExecAction(agentExePath, null, Path.GetDirectoryName(agentExePath)));
        ts.RootFolder.RegisterTaskDefinition(TaskName, td, TaskCreation.CreateOrUpdate,
            System.Security.Principal.WindowsIdentity.GetCurrent().Name, null, TaskLogonType.InteractiveToken);
    }

    public static void Unregister()
    {
        using var ts = new TaskService();
        if (ts.GetTask(TaskName) != null) ts.RootFolder.DeleteTask(TaskName, false);
    }
}

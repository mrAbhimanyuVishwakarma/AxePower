using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>A scheduled task and the programs its Exec actions start.</summary>
public sealed record ScheduledTaskInfo(string Path, string Name, IReadOnlyList<string> ExecPaths, string UserId, bool RunsHighest)
{
    /// <summary>
    /// True unless the task runs as the current user without "highest privileges"; deleting
    /// anyone else's task needs administrator rights.
    /// </summary>
    public bool RequiresElevation => RunsHighest || !IsCurrentUser(UserId);

    private static bool IsCurrentUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var name = identity.Name;                         // DOMAIN\user
            var shortName = name[(name.IndexOf('\\') + 1)..];  // user
            return userId.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                   userId.Equals(shortName, StringComparison.OrdinalIgnoreCase) ||
                   userId.Equals(identity.User?.Value, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Task Scheduler access through its COM API (<c>Schedule.Service</c>): enumerate tasks, read a
/// task's XML for backup, delete it, and register it again from that XML. Tasks under
/// \Microsoft are never listed.
/// </summary>
public static class ScheduledTaskService
{
    private const int TaskEnumHidden = 1;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonNone = 0;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskActionExec = 0;
    private const int TaskRunLevelHighest = 1;

    public static IReadOnlyList<ScheduledTaskInfo> EnumerateTasks(CancellationToken cancellationToken = default)
    {
        var results = new List<ScheduledTaskInfo>();
        dynamic? service = Connect();
        if (service is null) return results;

        try
        {
            Walk(service.GetFolder("\\"));
        }
        catch { }
        finally
        {
            Release(service);
        }
        return results;

        void Walk(dynamic folder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string folderPath = folder.Path;
            if (folderPath.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                foreach (dynamic task in folder.GetTasks(TaskEnumHidden))
                {
                    try
                    {
                        dynamic definition = task.Definition;
                        var exec = new List<string>();
                        foreach (dynamic action in definition.Actions)
                        {
                            if ((int)action.Type != TaskActionExec) continue;
                            string path = action.Path ?? string.Empty;
                            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                            if (path.Length > 0) exec.Add(path);
                        }

                        string userId = string.Empty;
                        var highest = false;
                        try
                        {
                            userId = definition.Principal.UserId ?? string.Empty;
                            highest = (int)definition.Principal.RunLevel == TaskRunLevelHighest;
                        }
                        catch { }

                        results.Add(new ScheduledTaskInfo((string)task.Path, (string)task.Name, exec, userId, highest));
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                foreach (dynamic sub in folder.GetFolders(0))
                    Walk(sub);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
    }

    /// <summary>The task's XML definition, or null when it doesn't exist or can't be read.</summary>
    public static string? GetXml(string taskPath)
    {
        dynamic? service = Connect();
        if (service is null) return null;
        try
        {
            var (folderPath, name) = Split(taskPath);
            dynamic task = service.GetFolder(folderPath).GetTask(name);
            return (string)task.Xml;
        }
        catch
        {
            return null;
        }
        finally
        {
            Release(service);
        }
    }

    public static bool Exists(string taskPath) => GetXml(taskPath) is not null;

    public static bool Delete(string taskPath)
    {
        dynamic? service = Connect();
        if (service is null) return false;
        try
        {
            var (folderPath, name) = Split(taskPath);
            service.GetFolder(folderPath).DeleteTask(name, 0);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(service);
        }
    }

    public static bool TryRegisterFromXmlFile(string taskPath, string xmlFile)
    {
        if (!File.Exists(xmlFile)) return false;
        dynamic? service = Connect();
        if (service is null) return false;
        try
        {
            var xml = File.ReadAllText(xmlFile);
            var (folderPath, name) = Split(taskPath);
            dynamic folder = service.GetFolder(folderPath);
            foreach (var logon in new[] { TaskLogonInteractiveToken, TaskLogonNone })
            {
                try
                {
                    folder.RegisterTask(name, xml, TaskCreateOrUpdate, null, null, logon, null);
                    return true;
                }
                catch { }
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(service);
        }
    }

    private static (string Folder, string Name) Split(string taskPath)
    {
        var index = taskPath.LastIndexOf('\\');
        var folder = index <= 0 ? "\\" : taskPath[..index];
        return (folder, taskPath[(index + 1)..]);
    }

    private static dynamic? Connect()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return null;
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            return service;
        }
        catch
        {
            return null;
        }
    }

    private static void Release(object? comObject)
    {
        try
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
                Marshal.FinalReleaseComObject(comObject);
        }
        catch { }
    }
}

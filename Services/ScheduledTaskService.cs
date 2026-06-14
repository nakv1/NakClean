namespace NakClean.Services;

public sealed class ScheduledTaskEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }     // полный путь задачи, напр. \Folder\Task
    public string Command { get; init; } = "";
    public string Author { get; init; } = "";
    public bool Enabled { get; set; }
}

/// <summary>
/// Запланированные задачи через COM «Schedule.Service» (без сторонних библиотек).
/// Показываем НЕ системные задачи (не из \Microsoft\) - как в CCleaner.
/// Включение/выключение/удаление требуют прав администратора для части задач.
/// </summary>
public static class ScheduledTaskService
{
    private static dynamic? Connect()
    {
        var t = Type.GetTypeFromProgID("Schedule.Service");
        if (t is null) return null;
        dynamic svc = Activator.CreateInstance(t)!;
        svc.Connect();
        return svc;
    }

    public static List<ScheduledTaskEntry> GetTasks()
    {
        var list = new List<ScheduledTaskEntry>();
        try
        {
            dynamic? svc = Connect();
            if (svc is null) return list;
            Walk(svc.GetFolder("\\"), list);
        }
        catch { }
        return list;
    }

    private static void Walk(dynamic folder, List<ScheduledTaskEntry> list)
    {
        try
        {
            foreach (dynamic task in folder.GetTasks(1)) // 1 = включая скрытые
            {
                try
                {
                    string path = task.Path;
                    if (path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))
                        continue; // системные задачи не трогаем

                    string cmd = "";
                    try
                    {
                        foreach (dynamic a in task.Definition.Actions)
                        {
                            try { cmd = a.Path; } catch { }
                            break;
                        }
                    }
                    catch { }

                    string author = "";
                    try { author = task.Definition.RegistrationInfo.Author ?? ""; } catch { }

                    list.Add(new ScheduledTaskEntry
                    {
                        Name = task.Name,
                        Path = path,
                        Command = cmd,
                        Author = author,
                        Enabled = task.Enabled,
                    });
                }
                catch { }
            }
        }
        catch { }

        try
        {
            foreach (dynamic sub in folder.GetFolders(0))
                Walk(sub, list);
        }
        catch { }
    }

    public static bool SetEnabled(string path, bool enabled)
    {
        try
        {
            dynamic? svc = Connect();
            if (svc is null) return false;
            int i = path.LastIndexOf('\\');
            string folderPath = i > 0 ? path[..i] : "\\";
            string name = path[(i + 1)..];
            dynamic folder = svc.GetFolder(folderPath);
            dynamic task = folder.GetTask(name);
            task.Enabled = enabled;
            return true;
        }
        catch { return false; }
    }

    public static bool Delete(string path)
    {
        try
        {
            dynamic? svc = Connect();
            if (svc is null) return false;
            int i = path.LastIndexOf('\\');
            string folderPath = i > 0 ? path[..i] : "\\";
            string name = path[(i + 1)..];
            dynamic folder = svc.GetFolder(folderPath);
            folder.DeleteTask(name, 0);
            return true;
        }
        catch { return false; }
    }
}

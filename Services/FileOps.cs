using System.IO;

namespace NakClean.Services;

/// <summary>
/// Безопасные файловые операции: всё обёрнуто в try/catch, заблокированные
/// или защищённые файлы просто пропускаются (честно считаем что реально можем удалить).
/// </summary>
public static class FileOps
{
    /// <summary>Рекурсивный обход файлов, устойчивый к ошибкам доступа.</summary>
    public static IEnumerable<string> SafeFiles(string root, string pattern, bool recursive)
    {
        if (!Directory.Exists(root)) yield break;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir, pattern); }
            catch { continue; }

            foreach (var f in files)
                yield return f;

            if (!recursive) continue;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var s in subs)
                stack.Push(s);
        }
    }

    /// <summary>Считает суммарный размер и количество файлов (без удаления).</summary>
    public static (long bytes, int files) Measure(
        IEnumerable<string> roots, string pattern, bool recursive, CancellationToken ct = default)
    {
        long bytes = 0;
        int files = 0;
        foreach (var root in roots)
        {
            foreach (var f in SafeFiles(root, pattern, recursive))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    bytes += new FileInfo(f).Length;
                    files++;
                }
                catch { /* файл исчез/недоступен */ }
            }
        }
        return (bytes, files);
    }

    /// <summary>Удаляет файлы. Возвращает (освобождено байт, удалено файлов, ошибок).</summary>
    public static (long freed, int deleted, int errors) Delete(
        IEnumerable<string> roots, string pattern, bool recursive,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        long freed = 0;
        int deleted = 0, errors = 0;

        foreach (var root in roots)
        {
            foreach (var f in SafeFiles(root, pattern, recursive))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(f);
                    long len = fi.Length;
                    if (fi.IsReadOnly) fi.IsReadOnly = false;
                    fi.Delete();
                    freed += len;
                    deleted++;
                    progress?.Report(freed);
                }
                catch { errors++; }
            }

            // подчищаем опустевшие подпапки (корень не трогаем)
            if (recursive && pattern == "*")
                TryRemoveEmptyDirs(root, keepRoot: true);
        }

        return (freed, deleted, errors);
    }

    private static void TryRemoveEmptyDirs(string root, bool keepRoot)
    {
        if (!Directory.Exists(root)) return;
        string[] subs;
        try { subs = Directory.GetDirectories(root); }
        catch { return; }

        foreach (var sub in subs)
            TryRemoveEmptyDirs(sub, keepRoot: false);

        if (keepRoot) return;
        try
        {
            if (!Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch { /* занято - пропускаем */ }
    }
}

using System;
using System.IO;
using System.Text;
using System.Threading;

namespace PuertsUnityMcp
{
    public static class AtomicFile
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly TimeSpan StaleTempRetention = TimeSpan.FromMinutes(1);

        public static void WriteAllText(string path, string text)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            CleanupStaleTempFiles(path, null);
            var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempPath, text ?? string.Empty, Utf8NoBom);

            Exception lastException = null;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        TryReplaceExistingFile(tempPath, path);
                    }
                    else
                    {
                        File.Move(tempPath, path);
                    }

                    CleanupStaleTempFiles(path, tempPath);
                    return;
                }
                catch (IOException ex)
                {
                    lastException = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastException = ex;
                }

                Thread.Sleep(25 * (attempt + 1));
            }

            TryDeleteTempFile(tempPath);
            CleanupStaleTempFiles(path, tempPath);
            throw lastException ?? new IOException("Failed to write file: " + path);
        }

        public static void WriteJson<T>(string path, T value, bool pretty = true)
        {
            WriteAllText(path, UnityJson.ToJson(value, pretty));
        }

        public static bool TryReadJson<T>(string path, out T value)
        {
            value = default;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                value = UnityJson.FromJson<T>(StripBom(json));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void AppendLine(string path, string line)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(path, (line ?? string.Empty) + Environment.NewLine, Utf8NoBom);
        }

        public static void EnsurePrivateDirectory(string path)
        {
            Directory.CreateDirectory(path);
            var gitignore = Path.Combine(path, ".gitignore");
            if (!File.Exists(gitignore))
            {
                WriteAllText(gitignore, "*" + Environment.NewLine + "!.gitignore" + Environment.NewLine);
            }
        }

        public static bool TryReadAllText(string path, out string text)
        {
            text = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                text = StripBom(File.ReadAllText(path, Encoding.UTF8));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string StripBom(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return content;
            }

            return content[0] == '\ufeff' ? content.Substring(1) : content;
        }

        private static void TryReplaceExistingFile(string tempPath, string path)
        {
            try
            {
                File.Replace(tempPath, path, null, true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (FileNotFoundException)
            {
                File.Move(tempPath, path);
                return;
            }

            File.Delete(path);
            File.Move(tempPath, path);
        }

        private static void TryDeleteTempFile(string tempPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }

        private static void CleanupStaleTempFiles(string path, string currentTempPath)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                var fileName = Path.GetFileName(path);
                if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName) || !Directory.Exists(directory))
                {
                    return;
                }

                var currentFullPath = string.IsNullOrEmpty(currentTempPath) ? string.Empty : Path.GetFullPath(currentTempPath);
                foreach (var tempPath in Directory.GetFiles(directory, fileName + ".tmp.*", SearchOption.TopDirectoryOnly))
                {
                    if (!string.IsNullOrEmpty(currentFullPath)
                        && string.Equals(Path.GetFullPath(tempPath), currentFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(tempPath) < StaleTempRetention)
                    {
                        continue;
                    }

                    TryDeleteTempFile(tempPath);
                }
            }
            catch
            {
            }
        }
    }
}

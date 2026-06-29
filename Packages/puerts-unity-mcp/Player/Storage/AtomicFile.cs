using System;
using System.IO;
using System.Text;

namespace PuertsUnityMcp
{
    public static class AtomicFile
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void WriteAllText(string path, string text)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempPath, text ?? string.Empty, Utf8NoBom);

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
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
    }
}

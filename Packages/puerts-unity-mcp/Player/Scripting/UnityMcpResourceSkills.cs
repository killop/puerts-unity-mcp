using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PuertsUnityMcp
{
    public static class UnityMcpResourceSkills
    {
        public static UnityMcpSkillDocument[] LoadSkills(string directoryRoot)
        {
            if (string.IsNullOrEmpty(directoryRoot) || !Directory.Exists(directoryRoot))
            {
                return new UnityMcpSkillDocument[0];
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directoryRoot, "*.*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityMCP] Failed to scan skill directory '" + directoryRoot + "': " + ex.Message);
                return new UnityMcpSkillDocument[0];
            }

            var result = new List<UnityMcpSkillDocument>();
            for (var i = 0; i < files.Length; i++)
            {
                var file = files[i];
                if (!IsSkillFile(file))
                {
                    continue;
                }

                try
                {
                    var skill = ParseSkill(directoryRoot, file, File.ReadAllText(file));
                    if (skill != null)
                    {
                        result.Add(skill);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[UnityMCP] Failed to parse skill '" + file + "': " + ex.Message);
                }
            }

            result.Sort((left, right) => string.CompareOrdinal(left.name, right.name));
            return result.ToArray();
        }

        public static UnityMcpSkillDocument FindSkill(string directoryRoot, string name)
        {
            var skills = LoadSkills(directoryRoot);
            for (var i = 0; i < skills.Length; i++)
            {
                if (string.Equals(skills[i].name, name, StringComparison.Ordinal))
                {
                    return skills[i];
                }
            }

            return null;
        }

        private static bool IsSkillFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            return string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase);
        }

        private static UnityMcpSkillDocument ParseSkill(string directoryRoot, string path, string text)
        {
            var trimmed = text.TrimStart();
            if (!trimmed.StartsWith("---", StringComparison.Ordinal))
            {
                return null;
            }

            var first = text.IndexOf("---", StringComparison.Ordinal);
            var second = text.IndexOf("---", first + 3, StringComparison.Ordinal);
            if (first < 0 || second < 0)
            {
                return null;
            }

            var frontMatter = text.Substring(first + 3, second - first - 3);
            var body = text.Substring(second + 3).TrimStart('\r', '\n');
            var skill = new UnityMcpSkillDocument
            {
                assetName = BuildRelativePath(directoryRoot, path),
                filePath = Path.GetFullPath(path),
                content = body
            };

            var lines = frontMatter.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    skill.name = TrimYamlScalar(line.Substring("name:".Length));
                }
                else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                {
                    skill.description = TrimYamlScalar(line.Substring("description:".Length));
                }
            }

            return string.IsNullOrEmpty(skill.name) ? null : skill;
        }

        private static string BuildRelativePath(string directoryRoot, string path)
        {
            try
            {
                var root = Path.GetFullPath(directoryRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                var fullPath = Path.GetFullPath(path);
                return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? fullPath.Substring(root.Length).Replace('\\', '/')
                    : Path.GetFileName(path);
            }
            catch
            {
                return Path.GetFileName(path);
            }
        }

        private static string TrimYamlScalar(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length >= 2
                && ((value[0] == '"' && value[value.Length - 1] == '"')
                    || (value[0] == '\'' && value[value.Length - 1] == '\'')))
            {
                return value.Substring(1, value.Length - 2);
            }

            return value;
        }
    }

    [Serializable]
    public sealed class UnityMcpSkillDocument
    {
        public string name;
        public string description;
        public string assetName;
        public string filePath;
        public string content;
    }

    [Serializable]
    public sealed class UnityMcpSkillListResult
    {
        public string action;
        public string targetId;
        public string resourceRoot;
        public string directoryRoot;
        public int count;
        public UnityMcpSkillDocument[] skills;
    }

    [Serializable]
    public sealed class UnityMcpSkillLoadResult
    {
        public string action;
        public string targetId;
        public string resourceRoot;
        public string directoryRoot;
        public bool success;
        public string error;
        public UnityMcpSkillDocument skill;
    }
}

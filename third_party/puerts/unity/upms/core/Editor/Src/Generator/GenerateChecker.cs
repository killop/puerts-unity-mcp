/*
* Tencent is pleased to support the open source community by making Puerts available.
* Copyright (C) 2020 Tencent.  All rights reserved.
* Puerts is licensed under the BSD 3-Clause License, except for the third-party components listed in the file 'LICENSE' which may be subject to their corresponding license terms. 
* This file is subject to the terms and conditions defined in file 'LICENSE', which is part of this source code package.
*/

#if UNITY_2020_1_OR_NEWER && !PUERTS_GENERAL
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Puerts.Editor.Generator
{
    /// <summary>
    /// Build preprocessor that checks if required IL2CPP files are generated before building
    /// </summary>
    public class GenerateChecker : IPreprocessBuildWithReport
    {
        public int callbackOrder => -100; // Run early in the build process

        private static readonly string[] RequiredFiles =
        {
            "Puerts_il2cpp.cpp",
            "PuertsIl2cppWrapper.cpp",
            "PuertsIl2cppBridge.cpp",
            "PuertsIl2cppFieldWrapper.cpp",
            "PuertsValueType.h",
            "TDataTrans.h",
            "pesapi.h",
            "pesapi_webgl.h",
            "pesapi_webgl.cpp",
            "unityenv_for_puerts.h"
        };

        public void OnPreprocessBuild(BuildReport report)
        {
            string saveTo = EnsureDirectoryPath(PathHelper.GetIl2cppPluginPath());
            List<string> missingFiles = GetMissingFiles(saveTo);

            if (missingFiles.Count > 0)
            {
                Debug.Log("[Puerts IL2CPP] Required reflection-mode files missing. Generating IL2CPP files at: " + saveTo + ". Missing: " + string.Join(", ", missingFiles.ToArray()));
                Directory.CreateDirectory(saveTo);
                try
                {
                    CSharpFileExporter.GenReflectionModeNativeFiles(saveTo);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[Puerts IL2CPP] Automatic native file generation failed: " + e);
                }
            }

            missingFiles = GetMissingFiles(saveTo);
            if (missingFiles.Count > 0)
            {
                string errorMessage = "[Puerts IL2CPP] Build cancelled. Required reflection-mode files not found at: " + saveTo + ". Missing: " + string.Join(", ", missingFiles.ToArray());
                Debug.LogError(errorMessage);
                throw new BuildFailedException(errorMessage);
            }

            Debug.Log("[Puerts IL2CPP] Generate check passed. Found reflection-mode files at: " + saveTo);
        }

        private static List<string> GetMissingFiles(string directory)
        {
            var missingFiles = new List<string>();
            foreach (string fileName in RequiredFiles)
            {
                if (!File.Exists(Path.Combine(directory, fileName)))
                {
                    missingFiles.Add(fileName);
                }
            }
            return missingFiles;
        }

        private static string EnsureDirectoryPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            char lastChar = path[path.Length - 1];
            if (lastChar == Path.DirectorySeparatorChar || lastChar == Path.AltDirectorySeparatorChar)
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }
    }
}
#endif

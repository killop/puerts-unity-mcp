(function () {
    function asString(value) {
        return value === null || typeof value === "undefined" ? "" : String(value);
    }

    function sceneNameFromPath(path) {
        var normalized = asString(path).replace(/\\/g, "/");
        var slash = normalized.lastIndexOf("/");
        var fileName = slash >= 0 ? normalized.substring(slash + 1) : normalized;
        var dot = fileName.lastIndexOf(".");
        return dot > 0 ? fileName.substring(0, dot) : fileName;
    }

    function callStatic(typeName, methodName) {
        return mcp.invokeStatic.apply(mcp, arguments);
    }

    function getStaticPath(memberPath) {
        return mcp.getStaticPath("UnityEditor.EditorBuildSettings", memberPath);
    }

    function assetExists(path) {
        if (!path) {
            return false;
        }

        try {
            return callStatic("UnityEditor.AssetDatabase", "LoadMainAssetAtPath", path) !== null;
        } catch (_) {
            return false;
        }
    }

    function assetGuid(path) {
        if (!path) {
            return "";
        }

        try {
            return asString(callStatic("UnityEditor.AssetDatabase", "AssetPathToGUID", path));
        } catch (_) {
            return "";
        }
    }

    var mcp = globalThis.__unity_mcp;
    if (!mcp || typeof mcp.getStaticPath !== "function") {
        throw new Error("__unity_mcp.getStaticPath is required for this JavaScript MCP tool.");
    }

    var count = Number(getStaticPath("scenes.length") || 0);
    var result = {
        sceneCount: count,
        hasStartupScene: count > 0,
        startupScene: null
    };

    if (count <= 0) {
        return JSON.stringify(result);
    }

    var path = asString(getStaticPath("scenes[0].path"));
    var enabled = !!getStaticPath("scenes[0].enabled");

    result.startupScene = {
        buildIndex: 0,
        isDefaultStartupScene: true,
        path: path,
        name: sceneNameFromPath(path),
        enabled: enabled,
        guid: assetGuid(path),
        assetExists: assetExists(path)
    };

    return JSON.stringify(result);
})()

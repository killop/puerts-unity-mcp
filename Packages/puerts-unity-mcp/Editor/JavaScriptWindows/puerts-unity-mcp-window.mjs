(function () {
    var state = {
        tab: 0
    };

    function stringOf(value) {
        return value === null || typeof value === "undefined" ? "" : String(value);
    }

    function snapshot(ctx) {
        return JSON.parse(ctx.SnapshotJson());
    }

    function setString(ctx, cfg, key, label) {
        var current = stringOf(cfg[key]);
        var next = ctx.TextField(label, current);
        if (next !== current) {
            ctx.SetConfigString(key, next);
        }
    }

    function setInt(ctx, cfg, key, label) {
        var current = Number(cfg[key] || 0);
        var next = ctx.IntField(label, current);
        if (next !== current) {
            ctx.SetConfigInt(key, next);
        }
    }

    function setBool(ctx, cfg, key, label) {
        var current = !!cfg[key];
        var next = ctx.Toggle(label, current);
        if (next !== current) {
            ctx.SetConfigBool(key, next);
        }
    }

    function pathRow(ctx, label, path, open) {
        ctx.BeginHorizontal();
        ctx.BoldLabel(label);
        if (ctx.Button("Open")) {
            open();
        }
        ctx.EndHorizontal();
        ctx.SelectableLabel(stringOf(path));
        ctx.Space(4);
    }

    function drawHeader(ctx, snap) {
        ctx.BoldLabel("PuerTS Unity MCP");
        ctx.HelpBox("JavaScript-driven EditorWindow. Unity owns the window lifecycle; this script owns the UI and actions.", "info");
        ctx.BeginToolbar();
        if (ctx.ToolbarButton("Refresh", 72)) {
            ctx.ReloadConfig();
            ctx.Repaint();
        }
        if (ctx.ToolbarButton("Reload JS", 84)) {
            ctx.ReloadScript();
        }
        if (ctx.ToolbarButton("C# Settings", 92)) {
            ctx.OpenCSharpSettings();
        }
        ctx.FlexibleSpace();
        ctx.MiniLabel((snap.editorRunning ? "Editor MCP running" : "Editor MCP stopped") + " | JS " + snap.scriptSource);
        ctx.EndToolbar();
        state.tab = ctx.Toolbar(state.tab, "Server|Runtime|Targets|Paths|Script");
        ctx.Space(6);
    }

    function drawServer(ctx, snap) {
        var cfg = snap.config || {};
        ctx.BoldLabel("Editor MCP");
        ctx.Label("Status: " + (snap.editorRunning ? "Running" : "Stopped"));
        ctx.Label("Endpoint ID: " + stringOf(snap.editorEndpointId || "(not created)"));
        ctx.Label("Health URL:");
        ctx.SelectableLabel(snap.editorHealthUrl);
        ctx.Space(8);

        ctx.BeginHorizontal();
        ctx.BeginDisabled(snap.editorRunning);
        if (ctx.Button("Start")) {
            ctx.StartEditorMcp();
        }
        ctx.EndDisabled();
        ctx.BeginDisabled(!snap.editorRunning);
        if (ctx.Button("Stop")) {
            ctx.StopEditorMcp();
        }
        ctx.EndDisabled();
        ctx.EndHorizontal();

        ctx.Space(10);
        ctx.BoldLabel("Startup");
        setBool(ctx, cfg, "editorAutoStart", "Auto Start With Unity");

        ctx.Space(10);
        ctx.BoldLabel("Network");
        setString(ctx, cfg, "editorBindAddress", "Bind Address");
        setInt(ctx, cfg, "editorPort", "Port");
        ctx.HelpBox("Use 0.0.0.0 when another computer should connect to this Editor MCP.", "none");

        ctx.Space(10);
        ctx.BoldLabel("LAN Discovery");
        setBool(ctx, cfg, "lanDiscoveryEnabled", "Enabled");
        setString(ctx, cfg, "name", "Name");
        setString(ctx, cfg, "name_group", "Name Group");
        ctx.HelpBox("UDP discovery can be blocked by firewall, AP isolation, VLAN routing, or network policy. Configure lanHttpProbeHosts or lanHttpProbeCidrs in editor-mcp-config.json for TCP/HTTP fallback.", "none");
    }

    function drawRuntime(ctx, snap) {
        var cfg = snap.config || {};
        ctx.BoldLabel("Play Mode / Player MCP");
        ctx.Label("Local Runtime: " + (snap.runtimeActive ? snap.runtimeEndpointId : "Not active"));
        ctx.Label("Local Runtime URL:");
        ctx.SelectableLabel(snap.runtimeHealthUrl || "(none)");
        ctx.Space(8);

        setBool(ctx, cfg, "runtimeAutoStart", "Auto Start Runtime Host");
        setString(ctx, cfg, "runtimeBindAddress", "Bind Address");
        setInt(ctx, cfg, "runtimePort", "Port");
        setInt(ctx, cfg, "runtimeLogBufferSize", "Log Buffer Size");
        ctx.HelpBox("Use 0.0.0.0 for APK/IPA/standalone LAN direct so a PC agent can connect to the embedded Player MCP.", "none");

        ctx.Space(10);
        if (ctx.Button("Write Runtime Extension Config")) {
            ctx.WriteRuntimeResourcesConfig();
        }
    }

    function drawTargets(ctx, snap) {
        ctx.BoldLabel("Selected Target");
        ctx.Label("Kind: " + stringOf(snap.selectedTargetKind || "editor"));
        ctx.Label("ID: " + stringOf(snap.selectedTargetId || "(not selected)"));
        ctx.Label("Name: " + stringOf(snap.selectedTargetName || "(not selected)"));
        ctx.Label("URL:");
        ctx.SelectableLabel(snap.selectedTargetUrl || "(through local Editor MCP)");

        ctx.Space(8);
        ctx.BeginHorizontal();
        if (ctx.Button("Refresh")) {
            ctx.Repaint();
        }
        ctx.BeginDisabled(!snap.editorRunning);
        if (ctx.Button("Scan LAN")) {
            ctx.ScanLanTargets();
        }
        ctx.EndDisabled();
        ctx.EndHorizontal();

        ctx.Space(10);
        ctx.BoldLabel("Available Targets");
        var targets = snap.targets || [];
        if (targets.length === 0) {
            ctx.HelpBox(snap.editorRunning ? "No targets are available yet." : "Start the Editor MCP to list targets.", "warning");
            return;
        }

        for (var i = 0; i < targets.length; i++) {
            var target = targets[i];
            var selected = target.kind === snap.selectedTargetKind && target.id === snap.selectedTargetId;
            ctx.BeginVerticalBox();
            ctx.BeginHorizontal();
            ctx.BoldLabel(target.name || "(unknown)");
            ctx.BeginDisabled(selected);
            if (ctx.Button(selected ? "Selected" : "Select")) {
                ctx.SelectTarget(target.index);
            }
            ctx.EndDisabled();
            ctx.EndHorizontal();
            ctx.Label("Kind: " + stringOf(target.kind));
            ctx.Label("Source: " + stringOf(target.source || "(unknown)"));
            ctx.Label("ID: " + stringOf(target.id));
            ctx.Label("Group: " + stringOf(target.group));
            ctx.Label("Platform: " + stringOf(target.platform));
            ctx.Label("URL:");
            ctx.SelectableLabel(target.url || "(through local Editor MCP)");
            ctx.EndVertical();
        }
    }

    function drawPaths(ctx, snap) {
        ctx.BoldLabel("Project Paths");
        pathRow(ctx, "Project Root", snap.projectRoot, function () { ctx.OpenProjectRoot(); });
        pathRow(ctx, "State Root", snap.stateRoot, function () { ctx.OpenStateRoot(); });
        pathRow(ctx, "Editor Config", snap.editorConfigPath, function () { ctx.OpenEditorConfig(); });
        pathRow(ctx, "Mobile Config", snap.runtimeConfigPath, function () { ctx.OpenRuntimeConfig(); });
        pathRow(ctx, "Agent Setup", snap.setupGuidePath, function () { ctx.OpenSetupGuide(); });
    }

    function drawScript(ctx, snap) {
        ctx.BoldLabel("JavaScript Window Script");
        ctx.Label("Source: " + snap.scriptSource);
        ctx.SelectableLabel(snap.scriptPath);
        ctx.Space(8);
        ctx.BeginHorizontal();
        if (ctx.Button("Open Script")) {
            ctx.OpenScript();
        }
        if (ctx.Button("Open Override Folder")) {
            ctx.OpenOverrideFolder();
        }
        if (ctx.Button("Create Project Override")) {
            ctx.CreateProjectOverrideScript();
        }
        ctx.EndHorizontal();
        ctx.HelpBox("Project override path: puerts-unity-mcp-extension/Editor/JavaScriptWindows/puerts-unity-mcp-window.mjs", "none");
        ctx.HelpBox("This window is driven by JavaScript. Editing the project override updates the UI without adding or compiling C#.", "none");
    }

    function onGUI(ctx) {
        var snap = snapshot(ctx);
        drawHeader(ctx, snap);
        if (snap.message) {
            ctx.HelpBox(snap.message, "none");
        }
        if (snap.scriptError) {
            ctx.HelpBox(snap.scriptError, "error");
        }

        ctx.BeginScrollView();
        if (state.tab === 0) {
            drawServer(ctx, snap);
        } else if (state.tab === 1) {
            drawRuntime(ctx, snap);
        } else if (state.tab === 2) {
            drawTargets(ctx, snap);
        } else if (state.tab === 3) {
            drawPaths(ctx, snap);
        } else {
            drawScript(ctx, snap);
        }
        ctx.EndScrollView();
    }

    function onEnable(ctx) {
        ctx.ClearMessage();
    }

    function onDisable(_) {
    }

    function onInspectorUpdate(_) {
    }

    globalThis.__unity_mcp_window_module = {
        onEnable: onEnable,
        onDisable: onDisable,
        onGUI: onGUI,
        onInspectorUpdate: onInspectorUpdate
    };
})();

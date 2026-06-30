(function () {
    var state = {
        year: new Date().getFullYear(),
        month: new Date().getMonth()
    };

    function monthName(month) {
        return [
            "January", "February", "March", "April", "May", "June",
            "July", "August", "September", "October", "November", "December"
        ][month];
    }

    function daysInMonth(year, month) {
        return new Date(year, month + 1, 0).getDate();
    }

    function shiftMonth(delta) {
        state.month += delta;
        while (state.month < 0) {
            state.month += 12;
            state.year -= 1;
        }

        while (state.month > 11) {
            state.month -= 12;
            state.year += 1;
        }
    }

    function drawHeader(ctx) {
        ctx.BoldLabel("Pure JavaScript Calendar");
        ctx.HelpBox("Copy this file to the project override path and rename it to puerts-unity-mcp-window.mjs to run it without compiling C#.", "none");
        ctx.BeginToolbar();
        if (ctx.ToolbarButton("Prev", 56)) {
            shiftMonth(-1);
        }
        if (ctx.ToolbarButton("Today", 64)) {
            var now = new Date();
            state.year = now.getFullYear();
            state.month = now.getMonth();
        }
        if (ctx.ToolbarButton("Next", 56)) {
            shiftMonth(1);
        }
        ctx.FlexibleSpace();
        ctx.MiniLabel(monthName(state.month) + " " + state.year);
        ctx.EndToolbar();
        ctx.Space(8);
    }

    function drawWeekHeader(ctx) {
        var names = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
        ctx.BeginHorizontal();
        for (var i = 0; i < names.length; i++) {
            ctx.BoldLabel(names[i]);
        }
        ctx.EndHorizontal();
    }

    function drawCalendar(ctx) {
        var firstDay = new Date(state.year, state.month, 1).getDay();
        var count = daysInMonth(state.year, state.month);
        var day = 1;
        var today = new Date();
        for (var row = 0; row < 6; row++) {
            ctx.BeginHorizontal();
            for (var col = 0; col < 7; col++) {
                var cellIndex = row * 7 + col;
                if (cellIndex < firstDay || day > count) {
                    ctx.Label(" ");
                    continue;
                }

                var label = String(day);
                if (today.getFullYear() === state.year && today.getMonth() === state.month && today.getDate() === day) {
                    label = "[" + label + "]";
                }

                ctx.Label(label);
                day += 1;
            }
            ctx.EndHorizontal();
        }
    }

    function onGUI(ctx) {
        drawHeader(ctx);
        ctx.BeginScrollView();
        drawWeekHeader(ctx);
        ctx.Space(4);
        drawCalendar(ctx);
        ctx.EndScrollView();
    }

    globalThis.__unity_mcp_window_module = {
        onEnable: function (_) {},
        onDisable: function (_) {},
        onInspectorUpdate: function (_) {},
        onGUI: onGUI
    };
})();

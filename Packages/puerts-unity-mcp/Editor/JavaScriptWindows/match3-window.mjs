(function () {
    var width = 8;
    var height = 8;
    var gems = ["R", "G", "B", "Y", "P"];
    var state = {};

    function randomGem() {
        return gems[Math.floor(Math.random() * gems.length)];
    }

    function newBoard() {
        state.board = [];
        state.selected = null;
        state.score = 0;
        state.message = "Pick adjacent gems to swap.";
        for (var y = 0; y < height; y++) {
            var row = [];
            for (var x = 0; x < width; x++) {
                row.push(randomGem());
            }
            state.board.push(row);
        }
        settle();
        state.score = 0;
    }

    function swap(ax, ay, bx, by) {
        var temp = state.board[ay][ax];
        state.board[ay][ax] = state.board[by][bx];
        state.board[by][bx] = temp;
    }

    function adjacent(a, b) {
        return Math.abs(a.x - b.x) + Math.abs(a.y - b.y) === 1;
    }

    function collectMatches() {
        var marks = {};
        for (var y = 0; y < height; y++) {
            var run = 1;
            for (var x = 1; x <= width; x++) {
                if (x < width && state.board[y][x] === state.board[y][x - 1]) {
                    run += 1;
                } else {
                    if (run >= 3) {
                        for (var rx = x - run; rx < x; rx++) {
                            marks[rx + "," + y] = true;
                        }
                    }
                    run = 1;
                }
            }
        }

        for (var x2 = 0; x2 < width; x2++) {
            var verticalRun = 1;
            for (var y2 = 1; y2 <= height; y2++) {
                if (y2 < height && state.board[y2][x2] === state.board[y2 - 1][x2]) {
                    verticalRun += 1;
                } else {
                    if (verticalRun >= 3) {
                        for (var ry = y2 - verticalRun; ry < y2; ry++) {
                            marks[x2 + "," + ry] = true;
                        }
                    }
                    verticalRun = 1;
                }
            }
        }

        return marks;
    }

    function removeMatches(marks) {
        var count = 0;
        for (var key in marks) {
            if (!Object.prototype.hasOwnProperty.call(marks, key)) {
                continue;
            }
            var parts = key.split(",");
            state.board[Number(parts[1])][Number(parts[0])] = null;
            count += 1;
        }
        return count;
    }

    function collapse() {
        for (var x = 0; x < width; x++) {
            var column = [];
            for (var y = height - 1; y >= 0; y--) {
                if (state.board[y][x]) {
                    column.push(state.board[y][x]);
                }
            }
            while (column.length < height) {
                column.push(randomGem());
            }
            for (var yy = height - 1; yy >= 0; yy--) {
                state.board[yy][x] = column[height - 1 - yy];
            }
        }
    }

    function settle() {
        var total = 0;
        while (true) {
            var marks = collectMatches();
            var count = removeMatches(marks);
            if (count === 0) {
                break;
            }
            total += count;
            collapse();
        }
        state.score += total * 10;
        return total;
    }

    function clickCell(x, y) {
        var current = { x: x, y: y };
        if (!state.selected) {
            state.selected = current;
            state.message = "Selected " + x + "," + y + ". Pick an adjacent gem.";
            return;
        }

        if (state.selected.x === x && state.selected.y === y) {
            state.selected = null;
            state.message = "Selection cleared.";
            return;
        }

        if (!adjacent(state.selected, current)) {
            state.selected = current;
            state.message = "Selected " + x + "," + y + ". Pick an adjacent gem.";
            return;
        }

        swap(state.selected.x, state.selected.y, x, y);
        var matched = settle();
        if (matched === 0) {
            swap(state.selected.x, state.selected.y, x, y);
            state.message = "No match. Swap reverted.";
        } else {
            state.message = "Matched " + matched + " gems.";
        }
        state.selected = null;
    }

    function cellLabel(x, y) {
        var value = state.board[y][x];
        if (state.selected && state.selected.x === x && state.selected.y === y) {
            return "[" + value + "]";
        }
        return " " + value + " ";
    }

    function drawBoard(ctx) {
        for (var y = 0; y < height; y++) {
            ctx.BeginHorizontal();
            for (var x = 0; x < width; x++) {
                if (ctx.ToolbarButton(cellLabel(x, y), 34)) {
                    clickCell(x, y);
                }
            }
            ctx.EndHorizontal();
        }
    }

    function onGUI(ctx) {
        if (!state.board) {
            newBoard();
        }

        ctx.BoldLabel("Pure JavaScript Match-3");
        ctx.Label("Score: " + state.score);
        ctx.HelpBox(state.message, "none");
        ctx.BeginToolbar();
        if (ctx.ToolbarButton("New Game", 88)) {
            newBoard();
        }
        if (ctx.ToolbarButton("Shuffle", 72)) {
            newBoard();
            state.message = "Board shuffled.";
        }
        ctx.EndToolbar();
        ctx.Space(8);
        ctx.BeginScrollView();
        drawBoard(ctx);
        ctx.EndScrollView();
    }

    globalThis.__unity_mcp_window_module = {
        onEnable: function (_) {},
        onDisable: function (_) {},
        onInspectorUpdate: function (_) {},
        onGUI: onGUI
    };
})();

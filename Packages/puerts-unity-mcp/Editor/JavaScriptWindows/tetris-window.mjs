(function () {
    var width = 10;
    var height = 18;
    var shapes = [
        [[0, 0], [1, 0], [0, 1], [1, 1]],
        [[-1, 0], [0, 0], [1, 0], [2, 0]],
        [[0, 0], [1, 0], [0, 1], [-1, 1]],
        [[0, 0], [-1, 0], [0, 1], [1, 1]],
        [[-1, 0], [0, 0], [1, 0], [0, 1]]
    ];
    var state = {};

    function emptyBoard() {
        var board = [];
        for (var y = 0; y < height; y++) {
            var row = [];
            for (var x = 0; x < width; x++) {
                row.push(".");
            }
            board.push(row);
        }
        return board;
    }

    function reset() {
        state.board = emptyBoard();
        state.score = 0;
        state.gameOver = false;
        spawn();
    }

    function spawn() {
        state.piece = {
            shape: shapes[Math.floor(Math.random() * shapes.length)],
            x: 4,
            y: 0
        };
        if (collides(0, 0, state.piece.shape)) {
            state.gameOver = true;
        }
    }

    function collides(dx, dy, shape) {
        for (var i = 0; i < shape.length; i++) {
            var x = state.piece.x + dx + shape[i][0];
            var y = state.piece.y + dy + shape[i][1];
            if (x < 0 || x >= width || y >= height) {
                return true;
            }
            if (y >= 0 && state.board[y][x] !== ".") {
                return true;
            }
        }
        return false;
    }

    function rotateShape(shape) {
        var next = [];
        for (var i = 0; i < shape.length; i++) {
            next.push([-shape[i][1], shape[i][0]]);
        }
        return next;
    }

    function move(dx, dy) {
        if (state.gameOver) {
            return;
        }
        if (!collides(dx, dy, state.piece.shape)) {
            state.piece.x += dx;
            state.piece.y += dy;
            return;
        }
        if (dy > 0) {
            lockPiece();
        }
    }

    function rotate() {
        if (state.gameOver) {
            return;
        }
        var next = rotateShape(state.piece.shape);
        if (!collides(0, 0, next)) {
            state.piece.shape = next;
        }
    }

    function hardDrop() {
        if (state.gameOver) {
            return;
        }

        while (!state.gameOver && !collides(0, 1, state.piece.shape)) {
            state.piece.y += 1;
        }
        lockPiece();
    }

    function lockPiece() {
        for (var i = 0; i < state.piece.shape.length; i++) {
            var x = state.piece.x + state.piece.shape[i][0];
            var y = state.piece.y + state.piece.shape[i][1];
            if (y >= 0 && y < height && x >= 0 && x < width) {
                state.board[y][x] = "#";
            }
        }
        clearLines();
        spawn();
    }

    function clearLines() {
        for (var y = height - 1; y >= 0; y--) {
            var full = true;
            for (var x = 0; x < width; x++) {
                if (state.board[y][x] === ".") {
                    full = false;
                    break;
                }
            }
            if (full) {
                state.board.splice(y, 1);
                var row = [];
                for (var i = 0; i < width; i++) {
                    row.push(".");
                }
                state.board.unshift(row);
                state.score += 100;
                y += 1;
            }
        }
    }

    function renderCell(x, y) {
        for (var i = 0; i < state.piece.shape.length; i++) {
            if (state.piece.x + state.piece.shape[i][0] === x && state.piece.y + state.piece.shape[i][1] === y) {
                return "[]";
            }
        }
        return state.board[y][x] === "#" ? "##" : "..";
    }

    function drawBoard(ctx) {
        for (var y = 0; y < height; y++) {
            var line = "";
            for (var x = 0; x < width; x++) {
                line += renderCell(x, y);
            }
            ctx.Label(line);
        }
    }

    function onGUI(ctx) {
        if (!state.board) {
            reset();
        }

        ctx.BoldLabel("Pure JavaScript Tetris");
        ctx.Label("Score: " + state.score + (state.gameOver ? "  Game Over" : ""));
        ctx.BeginToolbar();
        if (ctx.ToolbarButton("Left", 54)) {
            move(-1, 0);
        }
        if (ctx.ToolbarButton("Right", 58)) {
            move(1, 0);
        }
        if (ctx.ToolbarButton("Down", 58)) {
            move(0, 1);
        }
        if (ctx.ToolbarButton("Rotate", 66)) {
            rotate();
        }
        if (ctx.ToolbarButton("Drop", 56)) {
            hardDrop();
        }
        if (ctx.ToolbarButton("New", 52)) {
            reset();
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

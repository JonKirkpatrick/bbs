(function () {
    function parseState(rawState) {
        if (typeof rawState !== "string" || rawState.trim() === "") {
            return {};
        }
        try {
            var parsed = JSON.parse(rawState);
            return parsed && typeof parsed === "object" ? parsed : {};
        } catch (_) {
            return {};
        }
    }

    function asNumber(value, fallback) {
        var num = Number(value);
        return Number.isFinite(num) ? num : fallback;
    }

    function drawLegend(ctx, x, y) {
        var items = [
            { label: "Open", color: "#f3f6f9" },
            { label: "Wall", color: "#27374d" },
            { label: "Goal", color: "#2e7d32" },
            { label: "Lava", color: "#c62828" },
        ];

        ctx.font = "12px IBM Plex Mono";
        ctx.fillStyle = "#1b263b";
        ctx.fillText("Legend", x, y);

        for (var i = 0; i < items.length; i++) {
            var rowY = y + 12 + i * 18;
            ctx.fillStyle = items[i].color;
            ctx.fillRect(x, rowY, 12, 12);
            ctx.strokeStyle = "#6c7a89";
            ctx.strokeRect(x, rowY, 12, 12);
            ctx.fillStyle = "#243447";
            ctx.fillText(items[i].label, x + 18, rowY + 10);
        }
    }

    function renderGridworld(payload) {
        var canvas = payload.canvas;
        var ctx = payload.ctx;
        var frame = payload.frame || {};
        var state = parseState(frame.raw_state);

        if (!canvas || !ctx) {
            return false;
        }

        var rows = Array.isArray(state.map_rows) ? state.map_rows : [];
        if (!rows.length) {
            canvas.width = 760;
            canvas.height = 320;
            ctx.clearRect(0, 0, canvas.width, canvas.height);
            ctx.fillStyle = "#243447";
            ctx.font = "16px IBM Plex Mono";
            ctx.fillText("No gridworld state available", 24, 40);
            return true;
        }

        var height = rows.length;
        var width = typeof rows[0] === "string" ? rows[0].length : 0;
        if (width <= 0) {
            return true;
        }

        var tile = Math.max(26, Math.floor(460 / Math.max(width, height)));
        var gridW = tile * width;
        var gridH = tile * height;
        var sidebarX = gridW + 36;

        canvas.width = Math.max(760, sidebarX + 240);
        canvas.height = Math.max(340, gridH + 56);
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        ctx.fillStyle = "#eaf0f6";
        ctx.fillRect(0, 0, canvas.width, canvas.height);

        var palette = {
            ".": "#f3f6f9",
            "#": "#27374d",
            "G": "#2e7d32",
            "L": "#c62828",
        };

        var pos = state.position || {};
        var agentX = asNumber(pos.x, -1);
        var agentY = asNumber(pos.y, -1);

        for (var y = 0; y < height; y++) {
            var row = String(rows[y] || "");
            for (var x = 0; x < width; x++) {
                var symbol = row.charAt(x);
                var color = palette[symbol] || "#f3f6f9";
                var left = 16 + x * tile;
                var top = 20 + y * tile;

                ctx.fillStyle = color;
                ctx.fillRect(left, top, tile, tile);
                ctx.strokeStyle = "#9aa6b2";
                ctx.strokeRect(left, top, tile, tile);
            }
        }

        if (agentX >= 0 && agentY >= 0) {
            var cx = 16 + agentX * tile + tile / 2;
            var cy = 20 + agentY * tile + tile / 2;
            ctx.fillStyle = "#1565c0";
            ctx.beginPath();
            ctx.arc(cx, cy, Math.max(6, tile * 0.28), 0, Math.PI * 2);
            ctx.fill();
            ctx.strokeStyle = "#ffffff";
            ctx.lineWidth = 2;
            ctx.stroke();
        }

        var done = !!state.done;
        var infoLines = [
            "Map: " + String(state.map || "unknown"),
            "Episode: " + String(state.episode || 0),
            "Step: " + String(state.step || 0) + " / " + String(state.max_steps || 0),
            "Reward: " + String(state.reward || 0),
            "Return: " + String(state.episode_return || 0),
            "Terminal: " + (done ? "yes" : "no"),
            "Reason: " + String(state.terminal_reason || "in_progress"),
            "Legal Moves: " + (Array.isArray(state.legal_moves) ? state.legal_moves.join(", ") : ""),
        ];

        ctx.fillStyle = "#102a43";
        ctx.font = "700 20px Space Grotesk";
        ctx.fillText("Gridworld RL", sidebarX, 36);

        ctx.font = "13px IBM Plex Mono";
        for (var i = 0; i < infoLines.length; i++) {
            ctx.fillStyle = i === 5 && done ? "#8b0000" : "#243447";
            ctx.fillText(infoLines[i], sidebarX, 62 + i * 20);
        }

        drawLegend(ctx, sidebarX, 250);
        return true;
    }

    if (window.BBSViewerPluginRuntime && typeof window.BBSViewerPluginRuntime.register === "function") {
        window.BBSViewerPluginRuntime.register("gridworld_rl", {
            render: renderGridworld,
        });
    }
})();

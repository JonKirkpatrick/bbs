(function () {
    function parseState(rawState) {
        if (!rawState || typeof rawState !== "string") {
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

    function drawCard(ctx, x, y, w, h, label, value) {
        ctx.fillStyle = "#ffffff";
        ctx.strokeStyle = "#d8d1bf";
        ctx.lineWidth = 1;
        ctx.fillRect(x, y, w, h);
        ctx.strokeRect(x, y, w, h);

        ctx.fillStyle = "#556";
        ctx.font = "12px IBM Plex Mono";
        ctx.fillText(String(label), x + 10, y + 18);

        ctx.fillStyle = "#14213d";
        ctx.font = "700 24px Space Grotesk";
        ctx.fillText(String(value), x + 10, y + 50);
    }

    function renderGuessNumber(payload) {
        var canvas = payload.canvas;
        var ctx = payload.ctx;
        var frame = payload.frame || {};
        var spec = payload.spec || {};
        var state = parseState(frame.raw_state);

        if (!canvas || !ctx) {
            return false;
        }

        var attempts = asNumber(state.attempts, 0);
        var maxRange = asNumber(state.max_range, 100);
        var lastGuess = state.last_guess === null || state.last_guess === undefined ? "-" : String(state.last_guess);
        var feedback = String(state.feedback || "Waiting for guess...");
        var done = !!(state.done || frame.is_terminal);

        var view = state.viewer && typeof state.viewer === "object" ? state.viewer : {};
        var progress = view.progress && typeof view.progress === "object" ? view.progress : {};
        var progressValue = asNumber(progress.value, attempts);
        var progressMax = Math.max(1, asNumber(progress.max, Math.max(progressValue, 1)));
        var progressRatio = Math.max(0, Math.min(1, progressValue / progressMax));

        canvas.width = 760;
        canvas.height = 300;
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        ctx.fillStyle = "#f7f3e8";
        ctx.fillRect(0, 0, canvas.width, canvas.height);
        ctx.fillStyle = "#0b7285";
        ctx.fillRect(0, 0, canvas.width, 62);

        ctx.fillStyle = "#ffffff";
        ctx.font = "700 28px Space Grotesk";
        ctx.fillText("Guess The Number", 20, 40);
        ctx.font = "13px IBM Plex Mono";
        ctx.fillText("Client plugin renderer", 22, 56);

        ctx.fillStyle = "#fffaf0";
        ctx.strokeStyle = done ? "#1f7a1f" : "#d8d1bf";
        ctx.lineWidth = done ? 2 : 1;
        ctx.fillRect(20, 82, 720, 58);
        ctx.strokeRect(20, 82, 720, 58);

        ctx.fillStyle = "#334";
        ctx.font = "12px IBM Plex Mono";
        ctx.fillText("Status", 32, 100);
        ctx.fillStyle = "#14213d";
        ctx.font = "700 24px Space Grotesk";
        ctx.fillText(feedback, 32, 128);

        var barX = 20;
        var barY = 158;
        var barW = 720;
        var barH = 22;

        ctx.fillStyle = "#e9e5d9";
        ctx.fillRect(barX, barY, barW, barH);
        ctx.fillStyle = "#1f8ea3";
        ctx.fillRect(barX, barY, Math.round(barW * progressRatio), barH);
        ctx.strokeStyle = "#c8c2b0";
        ctx.lineWidth = 1;
        ctx.strokeRect(barX, barY, barW, barH);

        ctx.fillStyle = "#314";
        ctx.font = "12px IBM Plex Mono";
        ctx.fillText("Attempt pressure: " + progressValue + " / " + progressMax, barX, barY + 38);

        var hint = String(view.hint || ("Enter an integer move from 1 to " + maxRange));
        ctx.fillStyle = "#445";
        ctx.font = "12px IBM Plex Mono";
        ctx.fillText(hint, 360, barY + 38);

        drawCard(ctx, 20, 206, 170, 78, "Attempts", attempts);
        drawCard(ctx, 200, 206, 170, 78, "Last Guess", lastGuess);
        drawCard(ctx, 380, 206, 170, 78, "Range", "1 - " + maxRange);
        drawCard(ctx, 560, 206, 180, 78, "State", done ? "Complete" : "Active");

        if (spec && spec.kind === "raw-state") {
            return true;
        }

        return true;
    }

    if (window.BBSViewerPluginRuntime && typeof window.BBSViewerPluginRuntime.register === "function") {
        window.BBSViewerPluginRuntime.register("guess_number", {
            render: renderGuessNumber,
        });
    }
})();

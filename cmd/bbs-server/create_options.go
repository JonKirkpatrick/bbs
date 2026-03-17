package main

import (
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/JonKirkpatrick/bbs/games"
)

const defaultCreateTimeLimitMS = 1000

func isStrictBoolToken(raw string) bool {
	normalized := strings.ToLower(strings.TrimSpace(raw))
	return normalized == "true" || normalized == "false"
}

func normalizeCreateGameArgs(parts []string) []string {
	args := make([]string, 0, len(parts))
	for _, part := range parts {
		trimmed := strings.TrimSpace(part)
		if trimmed != "" {
			args = append(args, trimmed)
		}
	}
	return args
}

func resolveArenaRuntimeOptions(game games.GameInstance, rawTimeMS, rawAllowHandicap string) (time.Duration, bool, error) {
	timeLimitMS := defaultCreateTimeLimitMS
	rawTimeMS = strings.TrimSpace(rawTimeMS)
	if rawTimeMS != "" {
		parsed, err := strconv.Atoi(rawTimeMS)
		if err != nil || parsed <= 0 {
			return 0, false, fmt.Errorf("time limit must be a positive integer in milliseconds")
		}
		timeLimitMS = parsed
	}

	allowHandicap := false
	rawAllowHandicap = strings.TrimSpace(strings.ToLower(rawAllowHandicap))
	if rawAllowHandicap != "" {
		parsed, err := strconv.ParseBool(rawAllowHandicap)
		if err != nil {
			return 0, false, fmt.Errorf("allow_handicap must be true or false when provided")
		}
		allowHandicap = parsed
	}

	if !games.EnforceMoveClock(game) || !games.SupportsHandicap(game) {
		allowHandicap = false
	}

	return time.Duration(timeLimitMS) * time.Millisecond, allowHandicap, nil
}

func parseCreateCommand(parts []string) (games.GameInstance, []string, time.Duration, bool, error) {
	if len(parts) < 2 {
		return nil, nil, 0, false, fmt.Errorf("Usage: CREATE <type> [time_ms] [handicap_bool] [args...]")
	}

	gameType := strings.TrimSpace(parts[1])
	if gameType == "" {
		return nil, nil, 0, false, fmt.Errorf("Usage: CREATE <type> [time_ms] [handicap_bool] [args...]")
	}

	rawRemainder := normalizeCreateGameArgs(parts[2:])

	idx := 0
	rawTime := ""
	rawHandicap := ""

	if idx < len(rawRemainder) {
		if _, err := strconv.Atoi(strings.TrimSpace(rawRemainder[idx])); err == nil {
			rawTime = strings.TrimSpace(rawRemainder[idx])
			idx++
		}
	}
	if idx < len(rawRemainder) {
		tok := strings.TrimSpace(rawRemainder[idx])
		if isStrictBoolToken(tok) {
			rawHandicap = tok
			idx++
		}
	}

	optionArgs := normalizeCreateGameArgs(rawRemainder[idx:])
	rawArgs := append([]string(nil), rawRemainder...)

	gameWithOptions, errWithOptions := games.GetGame(gameType, optionArgs)
	gameRaw, errRaw := games.GetGame(gameType, rawArgs)

	useOptions := false
	consumedTokens := idx > 0
	if !consumedTokens {
		useOptions = true
	} else {
		switch {
		case errWithOptions == nil && errRaw != nil:
			useOptions = true
		case errWithOptions != nil && errRaw == nil:
			useOptions = false
		case errWithOptions == nil && errRaw == nil:
			// If both parse paths work, prefer raw args unless handicap was explicit
			// or no game args remain after consuming options.
			useOptions = rawHandicap != "" || len(optionArgs) == 0
		default:
			return nil, nil, 0, false, errWithOptions
		}
	}

	game := gameRaw
	gameArgs := rawArgs
	effectiveRawTime := ""
	effectiveRawHandicap := ""
	if useOptions {
		game = gameWithOptions
		gameArgs = optionArgs
		effectiveRawTime = rawTime
		effectiveRawHandicap = rawHandicap
	}

	if game == nil {
		if errWithOptions != nil {
			return nil, nil, 0, false, errWithOptions
		}
		return nil, nil, 0, false, errRaw
	}

	timeLimit, allowHandicap, err := resolveArenaRuntimeOptions(game, effectiveRawTime, effectiveRawHandicap)
	if err != nil {
		return nil, nil, 0, false, err
	}

	return game, gameArgs, timeLimit, allowHandicap, nil
}

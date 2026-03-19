package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"unicode"

	"github.com/JonKirkpatrick/bbs/games/pluginapi"
)

var allowedInputTypes = map[string]struct{}{
	"text":     {},
	"number":   {},
	"checkbox": {},
	"email":    {},
	"password": {},
	"search":   {},
	"tel":      {},
	"url":      {},
}

func main() {
	dirsFlag := flag.String("dirs", "plugins/games", "comma-separated directories containing plugin manifests (*.json)")
	failIfMissing := flag.Bool("fail-if-missing", false, "fail when no manifest files are found")
	flag.Parse()

	directories := splitCommaList(*dirsFlag)
	if len(directories) == 0 {
		fmt.Fprintln(os.Stderr, "error: no manifest directories were provided")
		os.Exit(1)
	}

	nameToManifest := make(map[string]string)
	totalManifests := 0
	hadErrors := false

	for _, directory := range directories {
		pattern := filepath.Join(directory, "*.json")
		files, err := filepath.Glob(pattern)
		if err != nil {
			hadErrors = true
			fmt.Fprintf(os.Stderr, "error: invalid glob %s: %v\n", pattern, err)
			continue
		}

		sort.Strings(files)
		if len(files) == 0 {
			fmt.Printf("info: no plugin manifests found in %s\n", directory)
			continue
		}

		for _, manifestPath := range files {
			totalManifests++
			errs := validateManifestFile(manifestPath, nameToManifest)
			if len(errs) == 0 {
				fmt.Printf("ok: %s\n", manifestPath)
				continue
			}

			hadErrors = true
			for _, item := range errs {
				fmt.Fprintf(os.Stderr, "error: %s: %s\n", manifestPath, item)
			}
		}
	}

	if totalManifests == 0 {
		if *failIfMissing {
			fmt.Fprintln(os.Stderr, "error: no plugin manifests were found")
			os.Exit(1)
		}
		fmt.Println("info: nothing to validate")
		return
	}

	if hadErrors {
		os.Exit(1)
	}

	fmt.Printf("validated %d plugin manifest(s)\n", totalManifests)
}

func splitCommaList(raw string) []string {
	parts := strings.Split(raw, ",")
	values := make([]string, 0, len(parts))
	for _, part := range parts {
		value := strings.TrimSpace(part)
		if value == "" {
			continue
		}
		values = append(values, filepath.Clean(value))
	}
	return values
}

func validateManifestFile(manifestPath string, nameToManifest map[string]string) []string {
	raw, err := os.ReadFile(manifestPath)
	if err != nil {
		return []string{fmt.Sprintf("failed reading file: %v", err)}
	}

	decoder := json.NewDecoder(bytes.NewReader(raw))
	decoder.DisallowUnknownFields()

	var manifest pluginapi.Manifest
	if err := decoder.Decode(&manifest); err != nil {
		return []string{fmt.Sprintf("invalid JSON shape: %v", err)}
	}

	if err := decoder.Decode(&struct{}{}); err != io.EOF {
		return []string{"manifest contains trailing JSON content"}
	}

	problems := make([]string, 0)

	if manifest.ProtocolVersion != 0 && manifest.ProtocolVersion != pluginapi.ProtocolVersion {
		problems = append(problems,
			fmt.Sprintf("protocol_version=%d is unsupported (expected 0 or %d)", manifest.ProtocolVersion, pluginapi.ProtocolVersion),
		)
	}

	name := strings.ToLower(strings.TrimSpace(manifest.Name))
	if name == "" {
		problems = append(problems, "name is required")
	} else {
		if !isIdentifier(name) {
			problems = append(problems, "name must use letters, numbers, dot, dash, or underscore")
		}
		if previous, exists := nameToManifest[name]; exists {
			problems = append(problems, fmt.Sprintf("duplicate name %q already declared in %s", name, previous))
		} else {
			nameToManifest[name] = manifestPath
		}
	}

	if strings.TrimSpace(manifest.Executable) == "" {
		problems = append(problems, "executable is required")
	}

	viewerClientEntry := strings.TrimSpace(manifest.ViewerClientEntry)
	if viewerClientEntry == "" {
		problems = append(problems, "viewer_client_entry is required")
	} else if strings.Contains(viewerClientEntry, "..") {
		problems = append(problems, "viewer_client_entry must not contain '..'")
	}

	argKeys := make(map[string]struct{})
	for idx, arg := range manifest.Args {
		prefix := fmt.Sprintf("args[%d]", idx)
		key := strings.TrimSpace(arg.Key)
		if key == "" {
			problems = append(problems, prefix+".key is required")
			continue
		}
		if !isIdentifier(key) {
			problems = append(problems, prefix+".key must use letters, numbers, dot, dash, or underscore")
		}
		if _, exists := argKeys[key]; exists {
			problems = append(problems, fmt.Sprintf("duplicate arg key %q", key))
		} else {
			argKeys[key] = struct{}{}
		}

		inputType := strings.ToLower(strings.TrimSpace(arg.InputType))
		if inputType == "" {
			inputType = "text"
		}
		if _, ok := allowedInputTypes[inputType]; !ok {
			problems = append(problems, fmt.Sprintf("%s.input_type %q is unsupported", prefix, arg.InputType))
		}
	}

	return problems
}

func isIdentifier(value string) bool {
	if value == "" {
		return false
	}
	for _, ch := range value {
		if unicode.IsLetter(ch) || unicode.IsDigit(ch) {
			continue
		}
		switch ch {
		case '-', '_', '.':
			continue
		default:
			return false
		}
	}
	return true
}

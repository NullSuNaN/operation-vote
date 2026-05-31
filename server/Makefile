# --- CONFIGURATION VARIABLES ---
PROJECT = server.csproj

# Build Configuration profiles (Debug or Release)
CONFIG = Release

# Standard output directory for compilation artifacts
PUB_DIR = dist

# Detect the Host Operating System
OS_NAME :=
ifeq ($(OS),Windows_NT)
    OS_NAME = Windows
else
    UNAME_S := $(shell uname -s)
    ifeq ($(UNAME_S),Linux)
        OS_NAME = Linux
    endif
    ifeq ($(UNAME_S),Darwin)
        OS_NAME = macOS
    endif
endif

.PHONY: all clean check-deps build-win build-linux publish-win publish-linux publishSingleFile-win publishSingleFile-linux help

# --- DEFAULT TARGET ---
all: help

# --- DEPENDENCY VALIDATION ENGINE ---
check-deps:
	@echo "==> Verifying platform infrastructure for $(OS_NAME)..."
	@if [ "$(OS_NAME)" = "Linux" ]; then \
		if ! command -v xdotool >/dev/null 2>&1; then \
			echo "❌ ERROR: 'xdotool' dependency is missing!"; \
			echo "👉 Please run: sudo apt update && sudo apt install -y xdotool"; \
			exit 1; \
		else \
			echo "✅ 'xdotool' found."; \
		fi \
	elif [ "$(OS_NAME)" = "macOS" ]; then \
		if ! command -v osascript >/dev/null 2>&1; then \
			echo "❌ ERROR: System 'osascript' shell binary missing or corrupted."; \
			exit 1; \
		else \
			echo "✅ 'osascript' found."; \
		fi \
	elif [ "$(OS_NAME)" = "Windows" ]; then \
		echo "✅ User32.dll subsystem available natively."; \
	fi

# --- CLEANING TARGET ---
clean:
	@echo "==> Purging server compilation cache and distribution artifacts..."
	rm -rf $(PUB_DIR)
	rm -rf bin/
	rm -rf obj/
	dotnet clean $(PROJECT)

# --- STANDARD BUILD TARGETS ---
build-win:
	@echo "==> Cross-compiling Server for Windows (win-x64)..."
	dotnet build $(PROJECT) --os win -c $(CONFIG)

build-linux: check-deps
	@echo "==> Compiling Server for Linux (linux-x64)..."
	dotnet build $(PROJECT) --os linux -c $(CONFIG)

# --- STANDARD PUBLISH TARGETS ---
publish-win:
	@echo "==> Publishing standard layout for Windows (with external runtimes folder)..."
	dotnet publish $(PROJECT) -c $(CONFIG) -r win-x64 --self-contained true -o $(PUB_DIR)/win

publish-linux: check-deps
	@echo "==> Publishing standard layout for Linux..."
	dotnet publish $(PROJECT) -c $(CONFIG) -r linux-x64 --self-contained true -o $(PUB_DIR)/linux

# --- SINGLE FILE PUBLISH TARGETS ---
publishSingleFile-win:
	@echo "==> Generating Single Standalone Opaque Executable for Windows..."
	dotnet publish $(PROJECT) -c $(CONFIG) -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o $(PUB_DIR)/single-file/win
	@echo "Success! Windows binary generated at: server/$(PUB_DIR)/single-file/win/server.exe" [cite: 3]

publishSingleFile-linux: check-deps
	@echo "==> Generating Single Standalone Opaque Binary for Linux..."
	dotnet publish $(PROJECT) -c $(CONFIG) -r linux-x64 --self-contained true /p:PublishSingleFile=true -o $(PUB_DIR)/single-file/linux
	@echo "Success! Linux binary generated at: server/$(PUB_DIR)/single-file/linux/server" [cite: 4]

# --- HELPER MENU ---
help:
	@echo "========================================================================"
	@echo "                    server AUTOMATION DIRECTIVES                 "
	@echo "========================================================================"
	@echo "Detected Environment OS: $(OS_NAME)"
	@echo "========================================================================"
	@echo "Commands:"
	@echo "  make check-deps          - Asserts local native injection toolchains match"
	@echo "  make clean               - Wipes out local bin/, obj/, and dist/ folders" [cite: 5]
	@echo "  make build-win           - Cross-builds window assembly for Windows" [cite: 5]
	@echo "  make build-linux         - Builds window assembly for Linux" [cite: 5]
	@echo "  make publish-win         - Packages standard Windows layout folder structural sets" [cite: 5]
	@echo "  make publish-linux       - Packages standard Linux layout folder structural sets" [cite: 5]
	@echo "  make publishSingleFile-win   - Bakes native libSkiaSharp directly into ONE .exe" [cite: 5]
	@echo "  make publishSingleFile-linux - Bakes native libSkiaSharp directly into ONE binary" [cite: 5]
	@echo "========================================================================"
# --- CONFIGURATION VARIABLES ---
PROJECT = client-window.csproj

# Build Configuration profiles (Debug or Release)
CONFIG = Release

# Standard output directory for compilation artifacts
PUB_DIR = dist

.PHONY: all clean build-win build-linux publish-win publish-linux publishSingleFile-win publishSingleFile-linux help

# --- DEFAULT TARGET ---
all: help

# --- CLEANING TARGET ---
clean:
	@echo "==> Purging client compilation cache and distribution artifacts..."
	rm -rf $(PUB_DIR)
	rm -rf bin/
	rm -rf obj/
	dotnet clean $(PROJECT)

# --- STANDARD BUILD TARGETS ---
build-win:
	@echo "==> Cross-compiling Client for Windows (win-x64)..."
	dotnet build $(PROJECT) --os win -c $(CONFIG)

build-linux:
	@echo "==> Compiling Client for Linux (linux-x64)..."
	dotnet build $(PROJECT) --os linux -c $(CONFIG)

# --- STANDARD PUBLISH TARGETS ---
publish-win:
	@echo "==> Publishing standard layout for Windows (with external runtimes folder)..."
	dotnet publish $(PROJECT) -c $(CONFIG) -r win-x64 --self-contained true -o $(PUB_DIR)/win

publish-linux:
	@echo "==> Publishing standard layout for Linux..."
	dotnet publish $(PROJECT) -c $(CONFIG) -r linux-x64 --self-contained true -o $(PUB_DIR)/linux

# --- SINGLE FILE PUBLISH TARGETS ---
publishSingleFile-win:
	@echo "==> Generating Single Standalone Opaque Executable for Windows..."
	dotnet publish $(PROJECT) -c $(CONFIG) -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o $(PUB_DIR)/single-file/win
	@echo "Success! Windows binary generated at: client-window/$(PUB_DIR)/single-file/win/client-window.exe"

publishSingleFile-linux:
	@echo "==> Generating Single Standalone Opaque Binary for Linux..."
	dotnet publish $(PROJECT) -c $(CONFIG) -r linux-x64 --self-contained true /p:PublishSingleFile=true -o $(PUB_DIR)/single-file/linux
	@echo "Success! Linux binary generated at: client-window/$(PUB_DIR)/single-file/linux/client-window"

# --- HELPER MENU ---
help:
	@echo "========================================================================"
	@echo "                    CLIENT-WINDOW AUTOMATION DIRECTIVES                 "
	@echo "========================================================================"
	@echo "Commands:"
	@echo "  make clean               - Wipes out local bin/, obj/, and dist/ folders"
	@echo "  make build-win           - Cross-builds window assembly for Windows"
	@echo "  make build-linux         - Builds window assembly for Linux"
	@echo "  make publish-win         - Packages standard Windows layout folder structural sets"
	@echo "  make publish-linux       - Packages standard Linux layout folder structural sets"
	@echo "  make publishSingleFile-win   - Bakes native libSkiaSharp directly into ONE .exe"
	@echo "  make publishSingleFile-linux - Bakes native libSkiaSharp directly into ONE binary"
	@echo "========================================================================"
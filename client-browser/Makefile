PROJECT = client-browser.csproj
CONFIG = Release
PUB_DIR = dist

RUN_ARGS := $(wordlist 2,$(words $(MAKECMDGOALS)),$(MAKECMDGOALS))
$(eval $(RUN_ARGS):;@:)

.PHONY: all clean build publish help

all: help

clean:
	@echo "==> Purging WebAssembly compilation targets..."
	rm -rf $(PUB_DIR)
	rm -rf bin/
	rm -rf obj/
	dotnet clean $(PROJECT)

build:
	@echo "==> Compiling WebAssembly Client App..."
	dotnet build $(PROJECT) -c $(CONFIG)

publish:
	@echo "==> Publishing WebAssembly static distribution bundle..."
	dotnet publish $(PROJECT) -c $(CONFIG) -o $(PUB_DIR)
	@echo ""
	@echo "========================================================================"
	@echo " Success! Production deployment bundle generated successfully."
	@echo " Serve the static files from the following directory:"
	@echo "   ./$(PUB_DIR)/wwwroot"
	@echo "========================================================================"

serve:
	dotnet serve -d:$(PUB_DIR)/wwwroot -p:$(RUN_ARGS) -a:0.0.0.0

help:
	@echo "========================================================================"
	@echo "                    CLIENT-BROWSER WASM AUTOMATION                      "
	@echo "========================================================================"
	@echo "Commands:"
	@echo "  make clean   - Wipes out temporary build files and outputs"
	@echo "  make build   - Compiles local files against WebAssembly profiles"
	@echo "  make publish - Compiles production-ready standalone static HTML/WASM outputs"
	@echo "  make serve <port> - Host the client website
	@echo "========================================================================"
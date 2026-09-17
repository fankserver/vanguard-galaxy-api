DOTNET ?= $(shell command -v dotnet 2>/dev/null || echo /tmp/dnsdk/dotnet/dotnet)
GAME_DIR ?= /mnt/c/Program Files (x86)/Steam/steamapps/common/Vanguard Galaxy
CONFIGURATION ?= Debug
TEST_EXCLUDE_CATEGORY ?=
TEST_ARGS ?=
TEST_FILTER = Category!=InstalledGame&Category!=Package$(if $(TEST_EXCLUDE_CATEGORY),&Category!=$(TEST_EXCLUDE_CATEGORY))
RELEASE_VERSION ?=
VERSION_ARG = $(if $(RELEASE_VERSION),-p:Version=$(RELEASE_VERSION))
MANAGED = $(GAME_DIR)/VanguardGalaxy_Data/Managed
CORE = $(GAME_DIR)/BepInEx/core

.PHONY: link-libs build test check-bindings package check-package check-local clean release-archive e2e
link-libs:
	@mkdir -p VGModAPI/lib
	@set -eu; for name in BepInEx 0Harmony; do test -f "$(CORE)/$$name.dll"; ln -sfn "$(CORE)/$$name.dll" "VGModAPI/lib/$$name.dll"; done
	@set -eu; for name in UnityEngine UnityEngine.CoreModule UnityEngine.UIModule UnityEngine.UI UnityEngine.ScreenCaptureModule Unity.TextMeshPro Unity.InputSystem; do test -f "$(MANAGED)/$$name.dll"; ln -sfn "$(MANAGED)/$$name.dll" "VGModAPI/lib/$$name.dll"; done
build: link-libs
	$(DOTNET) build VGModAPI.sln -c $(CONFIGURATION) $(VERSION_ARG)
	$(MAKE) build-examples
.PHONY: build-examples
# Every example package builds from its own folder, including the nested variant/consumer projects
# (StationCommerce/AuthorB, Observation/Consumers). One target covers them all; there are no
# per-example targets to keep in sync.
build-examples: link-libs
	@set -eu; for project in $$(find examples -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' | sort); do $(DOTNET) build "$$project" -c $(CONFIGURATION) $(VERSION_ARG); done
test:
	python3 -m unittest discover -s tools -p 'test_*.py'
	$(DOTNET) test VGModAPI.Tests/VGModAPI.Tests.csproj -c $(CONFIGURATION) --filter '$(TEST_FILTER)' $(TEST_ARGS)
check-bindings:
	VG_GAME_ASSEMBLY="$(MANAGED)/Assembly-CSharp.dll" $(DOTNET) test VGModAPI.Tests/VGModAPI.Tests.csproj -c $(CONFIGURATION) --filter 'Category=InstalledGame'
package: build
	@rm -rf artifacts/VGModAPI
	@mkdir -p artifacts/VGModAPI
	cp VGModAPI/bin/$(CONFIGURATION)/netstandard2.1/VGModAPI.dll artifacts/VGModAPI/
	cp VGModAPI/bin/$(CONFIGURATION)/netstandard2.1/VGModAPI.Core.dll artifacts/VGModAPI/
	cp VGModAPI/bin/$(CONFIGURATION)/netstandard2.1/VGModAPI.Abstractions.dll artifacts/VGModAPI/
	cp VGModAPI/bin/$(CONFIGURATION)/netstandard2.1/VGModAPI.Unity.dll artifacts/VGModAPI/
	cp README.md LICENSE artifacts/VGModAPI/
	python3 tools/local_update_metadata.py vgmodapi.vgmod.json artifacts/VGModAPI/vgmodapi.vgmod.json $(RELEASE_CHANNEL)
	@mkdir -p artifacts/VGModAPI/docs
	cp -R docs/reference docs/assets artifacts/VGModAPI/docs/
	$(MAKE) check-package
RELEASE_CHANNEL ?= experimental
EXAMPLE_VERSION ?= 1.0.0
release-archive:
	@printf '%s' '$(RELEASE_VERSION)' | grep -Eq '^[0-9]+\.[0-9]+(\.[0-9]+){0,2}$$' || { echo 'RELEASE_VERSION must come from the reviewed numeric tag.' >&2; exit 1; }
	$(MAKE) package RELEASE_VERSION='$(RELEASE_VERSION)' RELEASE_CHANNEL='$(RELEASE_CHANNEL)'
	python3 tools/release_archive.py --root artifacts/VGModAPI --output artifacts/VGModAPI-$(RELEASE_VERSION)-$(RELEASE_CHANNEL).zip
example-update-package: link-libs
	$(DOTNET) build examples/UpdateParticipant/UpdateParticipant.csproj -c $(CONFIGURATION) -p:ExampleVersion=$(EXAMPLE_VERSION)
	python3 tools/package_update_example.py examples/UpdateParticipant/bin/$(CONFIGURATION)/netstandard2.1/UpdateParticipant.dll examples/UpdateParticipant/vgmodapi.example.updates.vgmod.json artifacts/UpdateParticipant
	python3 tools/validate_update_package.py --repo example/mod --tag v$(EXAMPLE_VERSION) --plugin vgmodapi.example.updates --version $(EXAMPLE_VERSION) --channel stable --archive artifacts/UpdateParticipant.zip --assembly artifacts/UpdateParticipant/UpdateParticipant.dll --dotnet $(DOTNET)
check-package:
	python3 tools/release_archive.py --root artifacts/VGModAPI --validate-only
	VG_PACKAGE_ROOT="$(CURDIR)/artifacts/VGModAPI" $(DOTNET) test VGModAPI.Tests/VGModAPI.Tests.csproj -c $(CONFIGURATION) --filter 'Category=Package'
check-local:
	$(MAKE) test
	$(MAKE) package
	$(MAKE) check-bindings

# Opt-in only: the controller stages/restores the installation and owns the game process.
E2E_PYTHON ?= $(if $(WSL_INTEROP),py.exe -3,python3)
E2E_SAVE_DIR ?=
E2E_TIMEOUT ?= 900
E2E_CASE ?= fresh-session
E2E_BUILD ?= artifacts/e2e/plugin
E2E_RUNTIME ?= artifacts/e2e/run
E2E_PATH = $(if $(WSL_INTEROP),$(shell wslpath -aw "$(1)"),$(1))
.PHONY: e2e-build e2e
e2e-build: link-libs
	$(DOTNET) build VGModAPI/VGModAPI.csproj -c $(CONFIGURATION)
	@set -eu; for project in $$(find examples -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' | sort); do $(DOTNET) build "$$project" -c $(CONFIGURATION); done
	$(DOTNET) build VGModAPI.E2E/VGModAPI.E2E.csproj -c $(CONFIGURATION)
	@mkdir -p "$(E2E_BUILD)" "$(E2E_RUNTIME)"
	@set -eu; for dll in VGModAPI VGModAPI.Core VGModAPI.Abstractions VGModAPI.Unity; do cp "VGModAPI/bin/$(CONFIGURATION)/netstandard2.1/$$dll.dll" "$(E2E_BUILD)/"; done
	cp VGModAPI.E2E/bin/$(CONFIGURATION)/netstandard2.1/VGModAPI.E2E.dll "$(E2E_BUILD)/"
	cp VGModAPI.E2E/bin/$(CONFIGURATION)/netstandard2.1/Newtonsoft.Json.dll "$(E2E_BUILD)/"
	@set -eu; for dll in PocketWorlds CargoRecovery StoryMissions StationCommerce StationCommerceB UiSurfaces Observation EquipmentTargeting; do \
		f=$$(find examples -path '*/bin/$(CONFIGURATION)/netstandard2.1/'"$$dll"'.dll' | head -n 1); \
		if [ -n "$$f" ]; then cp "$$f" "$(E2E_BUILD)/"; else echo "e2e staging missing example dll: $$dll" >&2; exit 1; fi; \
	done
e2e: e2e-build
	$(E2E_PYTHON) tools/e2e.py --game-dir '$(call E2E_PATH,$(GAME_DIR))' --build-dir '$(call E2E_PATH,$(E2E_BUILD))' --runtime-dir '$(call E2E_PATH,$(E2E_RUNTIME))' $(if $(E2E_SAVE_DIR),--save-dir '$(call E2E_PATH,$(E2E_SAVE_DIR))') --case $(E2E_CASE) --launch --timeout $(E2E_TIMEOUT) $(addprefix --stage ,$(E2E_STAGE))
clean:
	$(DOTNET) clean VGModAPI.sln

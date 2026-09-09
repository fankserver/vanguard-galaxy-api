DOTNET ?= $(shell command -v dotnet 2>/dev/null || echo /tmp/dnsdk/dotnet/dotnet)
GAME_DIR ?= /mnt/c/Program Files (x86)/Steam/steamapps/common/Vanguard Galaxy
CONFIGURATION ?= Debug
TEST_EXCLUDE_CATEGORY ?=
TEST_ARGS ?=
TEST_FILTER = Category!=InstalledGame&Category!=Package$(if $(TEST_EXCLUDE_CATEGORY),&Category!=$(TEST_EXCLUDE_CATEGORY))
RELEASE_VERSION := $(shell python3 -c 'import xml.etree.ElementTree as E; print(E.parse("Directory.Build.props").findtext("PropertyGroup/Version"))')
MANAGED = $(GAME_DIR)/VanguardGalaxy_Data/Managed
CORE = $(GAME_DIR)/BepInEx/core

.PHONY: link-libs build test check-bindings package check-package check-local clean release-archive
link-libs:
	@mkdir -p VGModAPI/lib
	@set -eu; for name in BepInEx 0Harmony; do test -f "$(CORE)/$$name.dll"; ln -sfn "$(CORE)/$$name.dll" "VGModAPI/lib/$$name.dll"; done
	@set -eu; for name in UnityEngine UnityEngine.CoreModule UnityEngine.UIModule UnityEngine.UI Unity.TextMeshPro Unity.InputSystem; do test -f "$(MANAGED)/$$name.dll"; ln -sfn "$(MANAGED)/$$name.dll" "VGModAPI/lib/$$name.dll"; done
build: link-libs
	$(DOTNET) build VGModAPI.sln -c $(CONFIGURATION)
.PHONY: build-dungeon-example build-dungeon-author
build-dungeon-example:
	$(DOTNET) build examples/AuthoredDungeon/AuthoredDungeon.csproj -c $(CONFIGURATION)
build-dungeon-author: link-libs
	$(DOTNET) build examples/DungeonAuthor/DungeonAuthor.csproj -c $(CONFIGURATION)
.PHONY: build-forge-example build-forge-host
build-forge-example:
	$(DOTNET) build examples/ForgeInspector/ForgeInspector.csproj -c $(CONFIGURATION)
build-forge-host: link-libs
	$(DOTNET) build examples/ForgeInspectorHost/ForgeInspectorHost.csproj -c $(CONFIGURATION)
.PHONY: build-bar-authors
build-bar-authors: link-libs
	$(DOTNET) build examples/OwnedBarAuthorA/OwnedBarAuthorA.csproj -c $(CONFIGURATION)
	$(DOTNET) build examples/OwnedBarAuthorB/OwnedBarAuthorB.csproj -c $(CONFIGURATION)
.PHONY: build-story-authors
build-story-authors: link-libs
	$(DOTNET) build examples/OwnedStoryCampaign/OwnedStoryCampaign.csproj -c $(CONFIGURATION)
	$(DOTNET) build examples/OwnedStoryJob/OwnedStoryJob.csproj -c $(CONFIGURATION)
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
	cp README.md LICENSE artifacts/VGModAPI/
	python3 tools/local_update_metadata.py vgmodapi.vgmod.json artifacts/VGModAPI/vgmodapi.vgmod.json $(RELEASE_CHANNEL)
	@mkdir -p artifacts/VGModAPI/docs
	cp -R docs/reference docs/assets artifacts/VGModAPI/docs/
	$(MAKE) check-package
RELEASE_CHANNEL ?= experimental
EXAMPLE_VERSION ?= 1.0.0
release-archive: package
	python3 tools/release_archive.py --root artifacts/VGModAPI --output artifacts/VGModAPI-$(RELEASE_VERSION)-$(RELEASE_CHANNEL).zip
example-update-package: link-libs
	$(DOTNET) build examples/UpdateParticipant/UpdateParticipant.csproj -c $(CONFIGURATION) -p:ExampleVersion=$(EXAMPLE_VERSION)
	python3 tools/package_update_example.py examples/UpdateParticipant/bin/$(CONFIGURATION)/netstandard2.1/UpdateParticipant.dll examples/UpdateParticipant/vgmodapi.example.updates.vgmod.json artifacts/UpdateParticipant
	python3 tools/publish_update.py --repo example/mod --tag v$(EXAMPLE_VERSION) --plugin vgmodapi.example.updates --version $(EXAMPLE_VERSION) --channel stable --archive artifacts/UpdateParticipant.zip --assembly artifacts/UpdateParticipant/UpdateParticipant.dll --dotnet $(DOTNET)
check-package:
	python3 tools/release_archive.py --root artifacts/VGModAPI --validate-only
	VG_PACKAGE_ROOT="$(CURDIR)/artifacts/VGModAPI" $(DOTNET) test VGModAPI.Tests/VGModAPI.Tests.csproj -c $(CONFIGURATION) --filter 'Category=Package'
check-local:
	$(MAKE) test
	$(MAKE) package
	$(MAKE) check-bindings
clean:
	$(DOTNET) clean VGModAPI.sln

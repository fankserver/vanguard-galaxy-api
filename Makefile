DOTNET ?= $(shell command -v dotnet 2>/dev/null || echo /tmp/dnsdk/dotnet/dotnet)
GAME_DIR ?= /mnt/c/Program Files (x86)/Steam/steamapps/common/Vanguard Galaxy
CONFIGURATION ?= Debug
RELEASE_VERSION ?= 0.1.1
MANAGED = $(GAME_DIR)/VanguardGalaxy_Data/Managed
CORE = $(GAME_DIR)/BepInEx/core

.PHONY: link-libs build test check-bindings package check-package check-local provenance clean release-archive
link-libs:
	@mkdir -p VGModAPI/lib
	@set -eu; for name in BepInEx 0Harmony; do test -f "$(CORE)/$$name.dll"; ln -sfn "$(CORE)/$$name.dll" "VGModAPI/lib/$$name.dll"; done
	@set -eu; for name in UnityEngine UnityEngine.CoreModule; do test -f "$(MANAGED)/$$name.dll"; ln -sfn "$(MANAGED)/$$name.dll" "VGModAPI/lib/$$name.dll"; done
build: link-libs
	$(DOTNET) build VGModAPI.sln -c $(CONFIGURATION)
test:
	python3 -m unittest discover -s tools -p 'test_release_archive.py'
	$(DOTNET) test VGModAPI.Tests/VGModAPI.Tests.csproj -c $(CONFIGURATION) --filter 'Category!=InstalledGame&Category!=Package'
check-bindings:
	VG_GAME_ASSEMBLY="$(MANAGED)/Assembly-CSharp.dll" $(DOTNET) test VGModAPI.Tests/VGModAPI.Tests.csproj -c $(CONFIGURATION) --filter 'Category=InstalledGame'
package: build
	@rm -rf artifacts/VGModAPI
	@mkdir -p artifacts/VGModAPI
	cp VGModAPI/bin/$(CONFIGURATION)/netstandard2.1/VGModAPI.dll artifacts/VGModAPI/
	cp VGModAPI/bin/$(CONFIGURATION)/netstandard2.1/VGModAPI.Core.dll artifacts/VGModAPI/
	cp VGModAPI/bin/$(CONFIGURATION)/netstandard2.1/VGModAPI.Abstractions.dll artifacts/VGModAPI/
	cp README.md LICENSE artifacts/VGModAPI/
	@mkdir -p artifacts/VGModAPI/docs
	cp docs/*.md artifacts/VGModAPI/docs/
	$(MAKE) check-package
release-archive: package
	python3 tools/release_archive.py --root artifacts/VGModAPI --output artifacts/VGModAPI-$(RELEASE_VERSION)-experimental.zip
check-package:
	VG_PACKAGE_ROOT="$(CURDIR)/artifacts/VGModAPI" $(DOTNET) test VGModAPI.Tests/VGModAPI.Tests.csproj -c $(CONFIGURATION) --filter 'Category=Package'
check-local:
	$(MAKE) test
	$(MAKE) package
	$(MAKE) check-bindings
	$(MAKE) provenance
provenance:
	@python3 tools/reference-provenance.py --game-dir "$(GAME_DIR)" --dotnet "$(DOTNET)" --configuration "$(CONFIGURATION)"
clean:
	$(DOTNET) clean VGModAPI.sln

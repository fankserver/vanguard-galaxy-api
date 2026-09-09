DOTNET ?= $(shell command -v dotnet 2>/dev/null || echo /tmp/dnsdk/dotnet/dotnet)
GAME_DIR ?= /mnt/c/Program Files (x86)/Steam/steamapps/common/Vanguard Galaxy
CONFIGURATION ?= Debug
RELEASE_VERSION := $(shell python3 -c 'import xml.etree.ElementTree as E; print(E.parse("Directory.Build.props").findtext("PropertyGroup/Version"))')
MANAGED = $(GAME_DIR)/VanguardGalaxy_Data/Managed
CORE = $(GAME_DIR)/BepInEx/core

.PHONY: link-libs build test check-bindings check-consumer check-archive package check-package check-local provenance clean release-archive
link-libs:
	@mkdir -p VGModAPI/lib
	@set -eu; for name in BepInEx 0Harmony; do test -f "$(CORE)/$$name.dll"; ln -sfn "$(CORE)/$$name.dll" "VGModAPI/lib/$$name.dll"; done
	@set -eu; for name in UnityEngine UnityEngine.CoreModule UnityEngine.UIModule UnityEngine.ScreenCaptureModule UnityEngine.UI Unity.TextMeshPro Unity.InputSystem; do test -f "$(MANAGED)/$$name.dll"; ln -sfn "$(MANAGED)/$$name.dll" "VGModAPI/lib/$$name.dll"; done
build: link-libs
	$(DOTNET) build VGModAPI.sln -c $(CONFIGURATION)
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
	$(DOTNET) test VGModAPI.Tests/VGModAPI.Tests.csproj -c $(CONFIGURATION) --filter 'Category!=InstalledGame&Category!=InstalledConsumer&Category!=InstalledArchive&Category!=Package&Category!=WorldQualificationPackage'
check-bindings:
	VG_GAME_ASSEMBLY="$(MANAGED)/Assembly-CSharp.dll" $(DOTNET) test VGModAPI.Tests/VGModAPI.Tests.csproj -c $(CONFIGURATION) --filter 'Category=InstalledGame'
# Metadata evidence for the members the actual-consumer qualification probe reflects. Needs the
# owner-built consumer binary; it is never part of the default test run or of check-local.
# Cecil reads the candidate only, but decoding its custom-attribute ENUM arguments
# (BepInDependency flags, Newtonsoft NullValueHandling) requires resolving those two compile-only
# dependencies, so the resolver gets an explicit bounded search path: the installed BepInEx core,
# the installed Managed references and the local package copy of Newtonsoft. Override
# ANIMA_DEPENDENCY_DIRS (path-separator separated) when they live elsewhere; nothing is copied.
NUGET_PACKAGES_DIR ?= $(HOME)/.nuget/packages
NEWTONSOFT_DIR ?= $(lastword $(sort $(wildcard $(NUGET_PACKAGES_DIR)/newtonsoft.json/*/lib/netstandard2.0)))
check-consumer:
	@test -n "$(ANIMA_ASSEMBLY)$(ECHO_ASSEMBLY)" || (echo 'Set ANIMA_ASSEMBLY=/path/to/VGAnima.dll and/or ECHO_ASSEMBLY=/path/to/VGEcho.dll'; exit 1)
	VG_ANIMA_ASSEMBLY="$(ANIMA_ASSEMBLY)" VG_ECHO_ASSEMBLY="$(ECHO_ASSEMBLY)" \
	VG_CONSUMER_DEPENDENCY_DIRS="$(CORE):$(MANAGED):$(NEWTONSOFT_DIR):$(ANIMA_DEPENDENCY_DIRS):$(ECHO_DEPENDENCY_DIRS)" \
	$(DOTNET) test VGModAPI.Tests/VGModAPI.Tests.csproj -c $(CONFIGURATION) --filter '$(CONSUMER_FILTER)' -- RunConfiguration.TreatNoTestsAsError=true
# Select only supplied consumers; the explicit override remains useful for focused checks.
ifneq ($(strip $(ANIMA_ASSEMBLY)),)
ifneq ($(strip $(ECHO_ASSEMBLY)),)
CONSUMER_FILTER ?= Category=InstalledConsumer
else
CONSUMER_FILTER ?= Category=InstalledConsumer&FullyQualifiedName~InstalledAnimaTravelConsumerTests
endif
else
CONSUMER_FILTER ?= Category=InstalledConsumer&FullyQualifiedName~InstalledEchoTravelConsumerTests
endif
# READ-ONLY attestation of the pinned archived TravelJournal prebuilt. It never builds, edits,
# reactivates, migrates or bridges the archive: it reads the accepted binary (and its sibling PDB,
# which is never deployed) and confirms the launcher's pins describe it.
TRAVELJOURNAL_PDB ?= $(patsubst %.dll,%.pdb,$(TRAVELJOURNAL_ASSEMBLY))
check-archive:
	@test -n "$(TRAVELJOURNAL_ASSEMBLY)" || (echo 'Set TRAVELJOURNAL_ASSEMBLY=/path/to/VGTravelJournal.dll (and optionally TRAVELJOURNAL_PDB)'; exit 1)
	VG_TRAVELJOURNAL_ASSEMBLY="$(TRAVELJOURNAL_ASSEMBLY)" VG_TRAVELJOURNAL_PDB="$(TRAVELJOURNAL_PDB)" \
	VG_TRAVELJOURNAL_REPO="$(TRAVELJOURNAL_REPO)" \
	VG_GAME_ASSEMBLY="$(MANAGED)/Assembly-CSharp.dll" \
	VG_CONSUMER_DEPENDENCY_DIRS="$(CORE):$(MANAGED)" \
	$(DOTNET) test VGModAPI.Tests/VGModAPI.Tests.csproj -c $(CONFIGURATION) --filter 'Category=InstalledArchive' -- RunConfiguration.TreatNoTestsAsError=true
.PHONY: package-world-qualification
package-world-qualification: link-libs
	$(DOTNET) build tools/WorldQualificationApi/WorldQualificationApi.csproj -c $(CONFIGURATION)
	@rm -rf artifacts/WorldQualificationApi
	@mkdir -p artifacts/WorldQualificationApi
	cp tools/WorldQualificationApi/bin/$(CONFIGURATION)/netstandard2.1/VGModAPI.dll artifacts/WorldQualificationApi/
	cp tools/WorldQualificationApi/bin/$(CONFIGURATION)/netstandard2.1/VGModAPI.Core.dll artifacts/WorldQualificationApi/
	cp tools/WorldQualificationApi/bin/$(CONFIGURATION)/netstandard2.1/VGModAPI.Abstractions.dll artifacts/WorldQualificationApi/
	cp tools/WorldQualificationApi/README.md artifacts/WorldQualificationApi/
	VG_WORLD_QUALIFICATION_PACKAGE_ROOT="$(CURDIR)/artifacts/WorldQualificationApi" VG_QUALIFICATION_REFERENCE_DIR="$(CORE)" $(DOTNET) test VGModAPI.Tests/VGModAPI.Tests.csproj -c $(CONFIGURATION) --filter 'Category=WorldQualificationPackage' -- RunConfiguration.TreatNoTestsAsError=true

package: build
	@rm -rf artifacts/VGModAPI
	@mkdir -p artifacts/VGModAPI
	cp VGModAPI/bin/$(CONFIGURATION)/netstandard2.1/VGModAPI.dll artifacts/VGModAPI/
	cp VGModAPI/bin/$(CONFIGURATION)/netstandard2.1/VGModAPI.Core.dll artifacts/VGModAPI/
	cp VGModAPI/bin/$(CONFIGURATION)/netstandard2.1/VGModAPI.Abstractions.dll artifacts/VGModAPI/
	cp README.md LICENSE artifacts/VGModAPI/
	python3 tools/local_update_metadata.py vgmodapi.vgmod.json artifacts/VGModAPI/vgmodapi.vgmod.json $(RELEASE_CHANNEL)
	@mkdir -p artifacts/VGModAPI/docs
	cp docs/*.md artifacts/VGModAPI/docs/
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

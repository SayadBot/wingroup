PROJECT := src/WinGroup.csproj
CONFIG ?= Release
RID ?= win-x64
VERSION ?= 0.1.0-local
INFO_VERSION ?= $(VERSION)
OUT_DIR ?= publish/$(RID)
DIST_DIR ?= dist
INSTALLER_SCRIPT ?= installer/WinGroup.iss
INSTALLER_BASENAME ?= WinGroup-$(VERSION)-setup
PORTABLE_FILENAME ?= WinGroup-$(VERSION)-portable.exe
ISCC ?= C:/Program Files (x86)/Inno Setup 6/ISCC.exe

.PHONY: help setup icon restore build run publish portable installer clean

help:
	@printf "Targets:\n"
	@printf "  make setup    - install Node deps with pnpm\n"
	@printf "  make icon     - generate src/app.ico from icon.svg\n"
	@printf "  make restore  - restore .NET dependencies\n"
	@printf "  make build    - build the WinGroup app\n"
	@printf "  make run      - run the WinGroup app\n"
	@printf "  make publish  - publish single-file exe output\n"
	@printf "  make portable - create portable exe artifact\n"
	@printf "  make installer - build Inno Setup installer\n"
	@printf "  make clean    - clean .NET build artifacts\n"

setup:
	pnpm install

icon: setup
	node scripts/build-icon.mjs

restore:
	dotnet restore $(PROJECT)

build: icon restore
	dotnet build $(PROJECT) -c $(CONFIG)

run: icon
	dotnet run --project $(PROJECT)

publish: icon restore
	dotnet publish $(PROJECT) -c $(CONFIG) -r $(RID) --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -p:Version=$(VERSION) -p:InformationalVersion=$(INFO_VERSION) -o $(OUT_DIR)

portable: publish
	pwsh -NoProfile -Command "New-Item -ItemType Directory -Force -Path '$(DIST_DIR)' | Out-Null; Copy-Item '$(OUT_DIR)/WinGroup.exe' '$(DIST_DIR)/$(PORTABLE_FILENAME)' -Force"

installer: publish
	pwsh -NoProfile -Command "& '$(ISCC)' /DMyAppVersion='$(VERSION)' /DMyOutputBaseFilename='$(INSTALLER_BASENAME)' /DMyOutputDir='../$(DIST_DIR)' '$(INSTALLER_SCRIPT)'"

clean:
	dotnet clean $(PROJECT)

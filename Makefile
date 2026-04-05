PROJECT := src/WinGroup.csproj
CONFIG ?= Release
RID ?= win-x64
VERSION ?= 0.1.0-local
INFO_VERSION ?= $(VERSION)
OUT_DIR ?= publish/$(RID)

.PHONY: help setup icon restore build run publish clean

help:
	@printf "Targets:\n"
	@printf "  make setup    - install Node deps with pnpm\n"
	@printf "  make icon     - generate src/app.ico from icon.svg\n"
	@printf "  make restore  - restore .NET dependencies\n"
	@printf "  make build    - build the WinGroup app\n"
	@printf "  make run      - run the WinGroup app\n"
	@printf "  make publish  - publish single-file exe output\n"
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

clean:
	dotnet clean $(PROJECT)

.PHONY: all

setup:
	dotnet tool restore
	dotnet restore

dev:
	dotnet watch run --project firewall-updater

build:
	dotnet build

publish:
	dotnet publish -p firewall-updater --output ./publish

run: dev

test:
	dotnet test --project firewall-updater

quality:
	dotnet csharpier format .
 
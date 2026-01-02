.PHONY: all docker-build docker-publish

REGISTRY := registry.digitalocean.com/luggage
IMAGE := azure-firewall-updater
TAG ?= $(shell git rev-parse --short=7 HEAD)

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

docker-build:
	docker build -t $(REGISTRY)/$(IMAGE):$(TAG) .

docker-publish: docker-build
	docker push $(REGISTRY)/$(IMAGE):$(TAG)

push-tag:
	docker push $(REGISTRY)/$(IMAGE):$(TAG)
 
deploy:
	sed -i '' 's|image: $(REGISTRY)/$(IMAGE):.*|image: $(REGISTRY)/$(IMAGE):$(TAG)|' manifests/daemonset.yaml
	kubectl apply -f manifests/

all-local: docker-build push-tag deploy

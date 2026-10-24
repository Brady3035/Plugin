# manifest.json holds the version. Release files are named
# Moonbase.Silo-<version>-<os>-<arch>, which Silo installs as uploaded.
VERSION ?= $(shell sed -n 's/^  "version": "\(.*\)",$$/\1/p' manifest.json)
LDFLAGS := -s -w -X main.version=$(VERSION)
PLATFORMS := linux/amd64 linux/arm64

.PHONY: build test dist clean

build:
	CGO_ENABLED=0 go build -trimpath -ldflags "$(LDFLAGS)" -o dist/moonbase .

test:
	go vet ./...
	go test -race ./...

dist:
	rm -f dist/Moonbase.Silo-* dist/SHA256SUMS
	@for p in $(PLATFORMS); do \
		GOOS=$${p%/*} GOARCH=$${p#*/} CGO_ENABLED=0 go build -trimpath -ldflags "$(LDFLAGS)" \
			-o dist/Moonbase.Silo-$(VERSION)-$${p%/*}-$${p#*/} . || exit 1; \
	done
	cd dist && sha256sum Moonbase.Silo-* > SHA256SUMS

clean:
	rm -rf dist

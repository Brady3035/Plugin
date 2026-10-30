# Build from the Go directory: docker build -f cmd/plex/Dockerfile .
FROM golang:1.26-alpine AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
ARG VERSION=0.0.0-dev
RUN CGO_ENABLED=0 go build -trimpath -ldflags "-s -w -X main.version=${VERSION}" -o /out/moonbase-plex ./cmd/plex \
	&& mkdir -p /out/config

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /out/moonbase-plex /moonbase-plex
# A named volume takes this folder's owner, so the nonroot user can write it.
COPY --from=build --chown=65532:65532 /out/config /config
VOLUME /config
EXPOSE 32500
ENTRYPOINT ["/moonbase-plex"]

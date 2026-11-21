# syntax=docker/dockerfile:1.7
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4 AS build

ARG TARGETARCH
WORKDIR /src
COPY . .

RUN set -eux; \
    case "$TARGETARCH" in \
      amd64) runtime_id=linux-x64 ;; \
      arm64) runtime_id=linux-arm64 ;; \
      *) echo "Unsupported target architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    dotnet restore src/ScaleSet.Autoscaler/ScaleSet.Autoscaler.csproj \
      --locked-mode; \
    dotnet publish src/ScaleSet.Autoscaler/ScaleSet.Autoscaler.csproj \
      --configuration Release \
      --runtime "$runtime_id" \
      --no-restore \
      --self-contained false \
      -p:UseAppHost=false \
      --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:12b1135f5f8b3c0a214f2a680ff2033dff4f684f445d6c697d9524ceeb303586 AS runtime

ENV SCALESET__Controller__StateDirectory=/var/lib/scaleset
WORKDIR /app
RUN mkdir -p /var/lib/scaleset /licenses \
    && chown 10001:10001 /var/lib/scaleset
COPY --from=build --chown=10001:10001 /app/publish/ ./
COPY --chown=10001:10001 LICENSE THIRD-PARTY-NOTICES.md /licenses/

USER 10001:10001
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=30s --retries=3 \
    CMD ["dotnet", "ScaleSet.Autoscaler.dll", "health-probe"]
ENTRYPOINT ["dotnet", "ScaleSet.Autoscaler.dll"]
CMD ["run"]

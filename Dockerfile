# The SDK stage runs on the build machine's platform and cross-compiles for the target (TARGETARCH is amd64 or
# arm64 under buildx), so multi-arch images don't run the SDK under emulation. A plain `docker compose build`
# leaves TARGETARCH empty and publishes for the build machine.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Directory.Build.props PrivatePaste.slnx ./
COPY src/ src/
RUN dotnet publish src/PrivatePaste.Server -c Release -o /app ${TARGETARCH:+--os linux -a $TARGETARCH}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
LABEL org.opencontainers.image.title="PrivatePaste" \
      org.opencontainers.image.description="Self-hosted clipboard sync server with an encrypted, searchable history" \
      org.opencontainers.image.source="https://github.com/DynamicsNinja/privatepaste"
WORKDIR /app
COPY --from=build /app .
RUN mkdir /data && chown app:app /data
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
VOLUME /data
EXPOSE 8080
# No HEALTHCHECK: curl is not in the image. Configure the health check in Dokploy against /health.
ENTRYPOINT ["dotnet", "PrivatePaste.Server.dll"]

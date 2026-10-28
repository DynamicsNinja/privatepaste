FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props PrivatePaste.slnx ./
COPY src/ src/
RUN dotnet publish src/PrivatePaste.Server -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
RUN mkdir /data && chown app:app /data
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
VOLUME /data
EXPOSE 8080
# No HEALTHCHECK: curl is not in the image. Configure the health check in Dokploy against /health.
ENTRYPOINT ["dotnet", "PrivatePaste.Server.dll"]

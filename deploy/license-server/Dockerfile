# Sloomy Tweaks license server — production container.
# Build from the repository root:  docker build -f deploy/license-server/Dockerfile -t sloomy-license .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/SloomyTweaks.Licensing.Contracts/ src/SloomyTweaks.Licensing.Contracts/
COPY src/SloomyTweaks.Core/ src/SloomyTweaks.Core/
COPY src/SloomyTweaks.Data/ src/SloomyTweaks.Data/
COPY src/SloomyTweaks.LicenseServer/ src/SloomyTweaks.LicenseServer/
RUN dotnet publish src/SloomyTweaks.LicenseServer -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update && apt-get install -y --no-install-recommends libsqlite3-0 && rm -rf /var/lib/apt/lists/* \
    && useradd --system --uid 10001 --home /app sloomy && mkdir -p /data /secrets && chown sloomy /data
WORKDIR /app
COPY --from=build /app ./
USER sloomy
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    Sloomy__DatabasePath=/data/licenses.db \
    Sloomy__SigningKeyPath=/secrets/signing-key.pem \
    Sloomy__TrustForwardedHeaders=true
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s CMD ["dotnet", "--version"]
ENTRYPOINT ["dotnet", "SloomyTweaks.LicenseServer.dll"]

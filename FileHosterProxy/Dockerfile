FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["FileHosterProxy/FileHosterProxy.csproj", "."]
RUN dotnet restore "FileHosterProxy.csproj"

COPY FileHosterProxy/ .
RUN dotnet build "FileHosterProxy.csproj" -c Release -o /app/build

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is required by the HEALTHCHECK below (and by docker-compose's healthcheck override);
# the Ubuntu-based aspnet runtime image does not include curl or wget by default.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/build .

ENV ASPNETCORE_URLS=http://+:5001
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 5001
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:5001/health || exit 1

ENTRYPOINT ["dotnet", "FileHosterProxy.dll"]

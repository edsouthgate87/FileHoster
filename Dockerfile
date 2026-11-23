# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

WORKDIR /src

COPY ["FileHoster/FileHoster.csproj", "./"]
RUN dotnet restore "FileHoster.csproj"

COPY FileHoster/ .
RUN dotnet build "FileHoster.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "FileHoster.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

WORKDIR /app

# Create non-root user
RUN addgroup -g 1001 -S appuser && adduser -u 1001 -S appuser -G appuser

# Create data and logs directories
RUN mkdir -p /data/files /app/logs && chown -R appuser:appuser /data /app/logs

# Copy published app
COPY --from=publish /app/publish .

USER appuser

EXPOSE 5000

RUN echo "App startup" && dotnet --info

ENTRYPOINT ["sh", "-c", "echo 'Starting FileHoster...' && dotnet FileHoster.dll; EXIT_CODE=$?; echo \"Exit code: $EXIT_CODE\"; exit $EXIT_CODE"]

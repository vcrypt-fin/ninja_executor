FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /app

# Copy csproj and restore dependencies
COPY *.csproj ./
RUN dotnet restore

# Copy everything else and build
COPY . ./
RUN dotnet publish -c Release -o out \
    --os linux \
    --self-contained false \
    /p:PublishSingleFile=false

# Build runtime image
FROM mcr.microsoft.com/dotnet/runtime:6.0
WORKDIR /app
COPY --from=build /app/out ./

# Expose the port that the application runs on
EXPOSE 8003

ENTRYPOINT ["dotnet", "ninja_exec.dll"]

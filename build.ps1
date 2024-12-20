
# Navigate to the script's directory
Set-Location -Path (Split-Path -Parent $MyInvocation.MyCommand.Definition)

# Clean previous builds
dotnet clean

# Publish as a single-file, self-contained executable
dotnet publish -c Release -r win-x64 --self-contained `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:IncludeAllContentForSelfExtract=true `
    -o ./publish

Write-Host "Build completed. Executable is located in the 'publish' directory."
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ebay-adapter.csproj ./
RUN dotnet restore ./ebay-adapter.csproj

COPY . ./
RUN dotnet publish ./ebay-adapter.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out ./

# Recommended non-root UID in many clusters; can be overridden by Helm securityContext.
USER 65532:65532

ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "ebay-adapter.dll"]


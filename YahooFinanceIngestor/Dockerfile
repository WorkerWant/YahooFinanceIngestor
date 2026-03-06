FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["YahooFinanceIngestor.csproj", "."]
RUN dotnet restore "./YahooFinanceIngestor.csproj"

COPY . .
RUN dotnet publish "./YahooFinanceIngestor.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
ENV PLAYWRIGHT_SKIP_BROWSER_GC=1

COPY --from=build /app/publish .

# Install Chromium and the OS dependencies required by Playwright.
RUN dotnet YahooFinanceIngestor.dll --install-browser \
    && rm -rf /var/lib/apt/lists/*

COPY entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

ENTRYPOINT ["/app/entrypoint.sh"]

# syntax=docker/dockerfile:1

# ── Build: one solution, one publish (host + all 15 module libraries) ─────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Publish for one Linux RID so only that platform's native libraries (SkiaSharp, SqlClient) ship.
ARG TARGETARCH=amd64
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props .editorconfig Commonlyz.slnx ./
COPY src/ src/
RUN RID="linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64)" \
 && dotnet restore src/Commonlyz.Web/Commonlyz.Web.csproj -r "$RID" \
 && dotnet publish src/Commonlyz.Web/Commonlyz.Web.csproj -c Release -r "$RID" --self-contained false -o /app --no-restore /p:UseAppHost=false

# ── Runtime ───────────────────────────────────────────────────────────────────
# Chiseled = no shell or package manager, non-root by default.
# "-extra" adds ICU + tzdata: SqlClient needs ICU, and NodaTime/TimeZoneInfo need real time zones.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Commonlyz.Web.dll"]

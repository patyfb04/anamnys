# ── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Anamnys.sln ./
COPY src/Anamnys.Domain/Anamnys.Domain.csproj src/Anamnys.Domain/
COPY src/Anamnys.Application/Anamnys.Application.csproj src/Anamnys.Application/
COPY src/Anamnys.Infrastructure/Anamnys.Infrastructure.csproj src/Anamnys.Infrastructure/
COPY src/Anamnys.Api/Anamnys.Api.csproj src/Anamnys.Api/

RUN dotnet restore

COPY . .
RUN dotnet publish src/Anamnys.Api/Anamnys.Api.csproj \
    -c Release -o /app/publish --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install native libs required by Whisper.net and LLamaSharp, plus ffmpeg for audio transcoding
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgomp1 \
    ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

RUN mkdir -p /app/models /app/temp

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Anamnys.Api.dll"]

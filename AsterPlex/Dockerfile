# Aster for Plex. Build from AsterPlex/:  docker build -t aster-plex .
# Official images pass Aster's service app keys as build arguments (from the repository's Actions secrets).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG ASTER_SIMKL_CLIENT_ID=""
ARG ASTER_ANILIST_CLIENT_ID=""
ARG ASTER_MAL_CLIENT_ID=""
ARG ASTER_HARDCOVER_CLIENT_ID=""
ARG ASTER_LASTFM_API_KEY=""
ARG ASTER_LASTFM_SHARED_SECRET=""
WORKDIR /src
COPY Aster.Plex/Aster.Plex.csproj Aster.Plex/
RUN dotnet restore Aster.Plex
COPY Aster.Plex/ Aster.Plex/
RUN dotnet publish Aster.Plex -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASTER_DATA=/config ASTER_PORT=8098
VOLUME /config
EXPOSE 8098
USER app
ENTRYPOINT ["dotnet", "Aster.Plex.dll"]

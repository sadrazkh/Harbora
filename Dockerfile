# syntax=docker/dockerfile:1

# ---- Stage 1: build the embedded Vue islands + Tailwind into wwwroot/build ----
FROM node:22-alpine AS frontend
WORKDIR /web
COPY src/Harbora.Web/package*.json ./
RUN npm install
COPY src/Harbora.Web/ ./
RUN npm run build

# ---- Stage 2: restore + publish the .NET solution ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Harbora.slnx ./
COPY src/ ./src/
# Bring in the frontend build output so it's included in publish.
COPY --from=frontend /web/wwwroot/build ./src/Harbora.Web/wwwroot/build
RUN dotnet restore src/Harbora.Web/Harbora.Web.csproj
RUN dotnet publish src/Harbora.Web/Harbora.Web.csproj -c Release -o /app --no-restore

# ---- Stage 3: runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# LibGit2Sharp needs libgit2's native deps; git is handy for diagnostics.
RUN apt-get update && apt-get install -y --no-install-recommends libssl3 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Harbora.Web.dll"]

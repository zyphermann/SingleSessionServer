# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8880
EXPOSE 8881

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["SingleSessionServer.csproj", "."]
RUN dotnet restore "SingleSessionServer.csproj"
COPY . .
RUN dotnet publish "SingleSessionServer.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8880
ENTRYPOINT ["dotnet", "SingleSessionServer.dll"]

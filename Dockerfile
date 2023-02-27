FROM mcr.microsoft.com/dotnet/runtime:7.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["DbtHelper/DbtHelper.fsproj", "DbtHelper/"]
RUN dotnet restore "DbtHelper/DbtHelper.fsproj"
COPY . .
WORKDIR "/src/DbtHelper"
RUN dotnet build "DbtHelper.fsproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "DbtHelper.fsproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "DbtHelper.dll"]

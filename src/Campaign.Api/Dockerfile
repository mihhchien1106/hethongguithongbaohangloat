FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/Shared.Contracts/Shared.Contracts.csproj", "src/Shared.Contracts/"]
COPY ["src/Campaign.Api/Campaign.Api.csproj", "src/Campaign.Api/"]
RUN dotnet restore "src/Campaign.Api/Campaign.Api.csproj"
COPY ["src/Shared.Contracts/", "src/Shared.Contracts/"]
COPY ["src/Campaign.Api/", "src/Campaign.Api/"]
WORKDIR "/src/src/Campaign.Api"
RUN dotnet publish "Campaign.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Campaign.Api.dll"]

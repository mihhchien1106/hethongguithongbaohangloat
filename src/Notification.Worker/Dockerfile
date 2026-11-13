FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/Shared.Contracts/Shared.Contracts.csproj", "src/Shared.Contracts/"]
COPY ["src/Notification.Worker/Notification.Worker.csproj", "src/Notification.Worker/"]
RUN dotnet restore "src/Notification.Worker/Notification.Worker.csproj"
COPY ["src/Shared.Contracts/", "src/Shared.Contracts/"]
COPY ["src/Notification.Worker/", "src/Notification.Worker/"]
WORKDIR "/src/src/Notification.Worker"
RUN dotnet publish "Notification.Worker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Notification.Worker.dll"]
